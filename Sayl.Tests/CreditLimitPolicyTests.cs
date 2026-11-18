using Common.DTOs;
using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Exceptions;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// اختبارات سياسة حد الائتمان المركزية (نقطة الإنفاذ الوحيدة: ProcessPaymentAsync
/// داخل clsCheckoutService — تشمل البيع المباشر وإكمال الفاتورة المعلقة).
///
/// الدلالة المُصلَحة: CreditLimit = 0 يعني «الشراء بالدين ممنوع كليًا» (لا وضع
/// ائتمان غير محدود)، والموجب يسمح ضمن الحد فقط. تجاوز الحد يُعيد نتيجة منظمة
/// قابلة للتمييز آليًا (CreditApprovalRequiredResult) تتطلب موافقة المدير —
/// دون إتمام أو قيد أي أثر (فاتورة/دَين/مخزون/دفعات).
///
/// مصدر الرصيد المعتمد: دفتر Customers.Balance (يُحدَّث معاملاتيًا في البيع
/// والسداد والمرتجعات) — قراءة حيّة داخل المعاملة بـ UPDLOCK.
/// </summary>
[Collection("database")]
public class CreditLimitPolicyTests
{
    private readonly DatabaseFixture _fx;
    public CreditLimitPolicyTests(DatabaseFixture fx) => _fx = fx;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    /// <summary>بذرة + مخزون كافٍ للبيع.</summary>
    private async Task<TestSeed> SeedWithStockAsync(string marker, int qty = 100, decimal unitCost = 5m)
    {
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var purchase = new clsPurchaseService(db);
        var r = await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = $"B-{marker}", Quantity = qty, UnitCost = unitCost } }
        });
        Assert.True(r.IsSuccess, r.Message);
        return seed;
    }

    /// <summary>ضبط حد ائتمان العميل مباشرة (قبل اختبار السياسة نفسها).</summary>
    private static async Task SetLimitAsync(SaylDbContext db, int customerId, decimal limit)
        => await db.Customers.Where(c => c.PartyId == customerId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.CreditLimit, limit));

    /// <summary>قراءة رصيد العميل (بلا تتبع).</summary>
    private async Task<decimal> BalanceOfAsync(SaylDbContext db, int customerId)
        => await db.Customers.AsNoTracking().Where(c => c.PartyId == customerId)
            .Select(c => c.Balance ?? 0m).SingleAsync();

    /// <summary>طلب بيع 1 وحدة بسعر معين للعميل مع دفعة نقدية اختيارية.</summary>
    private static DtoCheckoutRequest SaleRequest(TestSeed seed, int? customerId, decimal unitPrice,
        decimal paid, decimal discountPercent = 0m, decimal taxPercent = 0m)
        => new()
        {
            CustomerId = customerId,
            UserId = seed.UserId,
            Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 1, UnitPrice = unitPrice } },
            Payment = new DtoPaymentInfo { Amount = paid, Method = "cash", PaymentDate = Today },
            DiscountPercent = discountPercent,
            TaxPercent = taxPercent
        };

    /// <summary>تأكيد أن النتيجة رفض منظّم «يتطلب موافقة المدير» بحقول صحيحة.</summary>
    private static void AssertApprovalRequired(Result<int> result, int customerId,
        decimal current, decimal limit, decimal newCredit)
    {
        Assert.False(result.IsSuccess);
        var approval = Assert.IsType<CreditApprovalRequiredResult<int>>(result);
        Assert.NotNull(approval.ApprovalInfo);
        Assert.Equal(customerId, approval.ApprovalInfo!.CustomerId);
        Assert.Equal(current, approval.ApprovalInfo.CurrentOutstanding);
        Assert.Equal(limit, approval.ApprovalInfo.CreditLimit);
        Assert.Equal(newCredit, approval.ApprovalInfo.NewCreditAmount);
        Assert.Equal(current + newCredit, approval.ApprovalInfo.ProjectedOutstanding);
        Assert.Equal(current + newCredit - limit, approval.ApprovalInfo.ExcessAmount);
        Assert.Contains("موافقة المدير", result.Message);
    }

    /// <summary>الرفض يترك قاعدة نظيفة: لا فاتورة بيع جديدة ولا دفعات ولا حركة مخزون
    /// ولا تغيّر مخزون ولا دَين (يُقارن بأعداد ما قبل العملية المرفوضة).</summary>
    private async Task AssertNoTraceAsync(SaylDbContext db, TestSeed seed, int userId,
        decimal inventoryBefore, decimal balanceBefore, int saleInvoicesBefore = 0, int saleMovementsBefore = 0)
    {
        Assert.Equal(saleInvoicesBefore, await db.Invoices.CountAsync(i => i.UserId == userId && i.Type == enInvoicesType.sale));
        Assert.Equal(0, await db.Payments.CountAsync(p => p.PartyId == seed.CustomerId && p.PartyType == 'C' && p.Type == enPaymentType.income && p.InvoiceId != null));
        Assert.Equal(inventoryBefore, await db.Inventories.AsNoTracking()
            .Where(v => v.ProductId == seed.ProductId).Select(v => v.QuantityAvailable ?? 0).SingleAsync());
        Assert.Equal(saleMovementsBefore, await db.StockMovements.CountAsync(m => m.ProductId == seed.ProductId && m.MovementType == enStockMovementType.sale));
        Assert.Equal(balanceBefore, await BalanceOfAsync(db, seed.CustomerId));
    }

    // ══ 1) بيع تحت الحد: ينجح والرصيد يُقيَّد ══
    [Fact]
    public async Task SaleBelowLimit_Succeeds_AndUpdatesBalance()
    {
        var seed = await SeedWithStockAsync($"cl1-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);

        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 60m, 0m));
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(60m, await BalanceOfAsync(db, seed.CustomerId));
    }

    // ══ 2) بيع يساوي الحد المتاح تمامًا: ينجح (الحد ليس صارمًا) ══
    [Fact]
    public async Task SaleExactlyAtRemainingLimit_Succeeds()
    {
        var seed = await SeedWithStockAsync($"cl2-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);

        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 100m, 0m));
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(100m, await BalanceOfAsync(db, seed.CustomerId));
    }

    // ══ 3) بيع يتجاوز الحد: نتيجة موافقة المدير بحقول صحيحة ══
    [Fact]
    public async Task SaleExceedingLimit_ReturnsApprovalRequired_WithNothingPersisted()
    {
        var seed = await SeedWithStockAsync($"cl3-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);
        decimal inventoryBefore = await db.Inventories.AsNoTracking()
            .Where(v => v.ProductId == seed.ProductId).Select(v => v.QuantityAvailable ?? 0).SingleAsync();

        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 150m, 0m));

        AssertApprovalRequired(r, seed.CustomerId, current: 0m, limit: 100m, newCredit: 150m);
        await AssertNoTraceAsync(db, seed, seed.UserId, inventoryBefore, balanceBefore: 0m);
    }

    // ══ 4) عميل بلا دَين حالي: القيم في النتيجة تبدأ من صفر ══
    [Fact]
    public async Task SaleWithNoCurrentDebt_ApprovalCarriesZeroCurrent()
    {
        var seed = await SeedWithStockAsync($"cl4-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 50m);

        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 80m, 0m));
        AssertApprovalRequired(r, seed.CustomerId, current: 0m, limit: 50m, newCredit: 80m);
    }

    // ══ 5) عميل عليه فواتير غير مسددة: الحالي يشمل دَينه السابق ══
    [Fact]
    public async Task SaleWithExistingUnpaidInvoices_CurrentOutstandingIncludesPriorDebt()
    {
        var seed = await SeedWithStockAsync($"cl5-{Guid.NewGuid():N}"[..12], qty: 100);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);

        // دَين سابق 40 (بيع آجل ناجح)
        var first = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 40m, 0m));
        Assert.True(first.IsSuccess, first.Message);

        // بيع ثانٍ 70: المشروع 110 > 100 — الحالي في النتيجة = 40
        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 70m, 0m));
        AssertApprovalRequired(r, seed.CustomerId, current: 40m, limit: 100m, newCredit: 70m);
    }

    // ══ 6) بيع بعد تحصيل دفعة مسجلة: المتاح يرتفع ══
    [Fact]
    public async Task SaleAfterRecordedPayment_PaymentFreesAvailableCredit()
    {
        var seed = await SeedWithStockAsync($"cl6-{Guid.NewGuid():N}"[..12]);
        int saleId, userId = seed.UserId;
        await using (var db = _fx.CreateContext())
        {
            await SetLimitAsync(db, seed.CustomerId, 100m);
            var sale = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 100m, 0m));
            Assert.True(sale.IsSuccess, sale.Message);
            saleId = sale.Data;
        }

        // تحصيل 60 → الرصيد 40 → بيع 60 ينجح (المشروع 100 = الحد)
        await using (var db = _fx.CreateContext())
        {
            var pay = await new clsPaymentService(db).PayInvoiceAsync(new DtoPaymentRequest
            { InvoiceId = saleId, PartyId = seed.CustomerId, Amount = 60m, PaymentDate = Today, Method = "cash", UserId = userId });
            Assert.True(pay.IsSuccess, pay.Message);

            var r = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 60m, 0m));
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(100m, await BalanceOfAsync(db, seed.CustomerId));
        }
    }

    // ══ 7) بيع بعد مرتجع عميل معتمد: المرتجع يخفض الرصيد فالمتاح يرتفع ══
    [Fact]
    public async Task SaleAfterApprovedReturn_ReturnFreesAvailableCredit()
    {
        var seed = await SeedWithStockAsync($"cl7-{Guid.NewGuid():N}"[..12]);
        int saleId;
        await using (var db = _fx.CreateContext())
        {
            await SetLimitAsync(db, seed.CustomerId, 100m);
            var sale = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 100m, 0m));
            Assert.True(sale.IsSuccess, sale.Message);
            saleId = sale.Data;
        }

        // مرتجع كامل (وحدة واحدة = كل الفاتورة) → الدين يهبط 0 → بيع جديد بالكامل ينجح
        await using (var db = _fx.CreateContext())
        {
            var ret = await new clsReturnService(db).CreateCustomerReturnAsync(new DtoReturnRequest
            { OriginalInvoiceId = saleId, UserId = seed.UserId, Items = { new DtoReturnItem { ProductId = seed.ProductId, Quantity = 1 } } });
            Assert.True(ret.IsSuccess, ret.Message);
            Assert.Equal(0m, await BalanceOfAsync(db, seed.CustomerId));

            var r = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 100m, 0m));
            Assert.True(r.IsSuccess, r.Message);
        }
    }

    // ══ 8) دفعة عامة غير مخصصة (فائض): تخفض الرصيد ولا تعود دَينًا ══
    [Fact]
    public async Task SaleWhenUnallocatedReceiptExists_IsEvaluatedOnLedgerBalance()
    {
        var seed = await SeedWithStockAsync($"cl8-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);

        // دَين 100 ثم دفعة عامة 30 بلا فواتير مستحقة بعدها (فائض غير مخصص)
        var sale = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 100m, 0m));
        Assert.True(sale.IsSuccess, sale.Message);
        var receipt = await new clsPaymentService(db).ReceiveCustomerPaymentAsync(new DtoPaymentRequest
        { PartyId = seed.CustomerId, Amount = 30m, PaymentDate = Today, Method = "cash", UserId = seed.UserId });
        Assert.True(receipt.IsSuccess, receipt.Message);
        Assert.Equal(70m, await BalanceOfAsync(db, seed.CustomerId));

        // الرصيد الدفتري 70 — بيع 30 ينجح (المشروع 100) وبيع 40 يتطلب اعتمادًا
        var ok = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 30m, 0m));
        Assert.True(ok.IsSuccess, ok.Message);
        Assert.Equal(100m, await BalanceOfAsync(db, seed.CustomerId));

        var over = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 10m, 0m));
        AssertApprovalRequired(over, seed.CustomerId, current: 100m, limit: 100m, newCredit: 10m);
    }

    // ══ 9) سداد مرتبط + تسوية عامة معًا: لا ازدواج في الرصيد المعتمد ══
    [Fact]
    public async Task LinkedPaymentAndGeneralAllocationCoexist_WithoutDoubleCounting()
    {
        var seed = await SeedWithStockAsync($"cl9-{Guid.NewGuid():N}"[..12]);
        int sale1, sale2;
        await using (var db = _fx.CreateContext())
        {
            await SetLimitAsync(db, seed.CustomerId, 150m);
            var s1 = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 100m, 0m));
            var s2 = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 50m, 0m));
            Assert.True(s1.IsSuccess && s2.IsSuccess, $"{s1.Message} / {s2.Message}");
            sale1 = s1.Data; sale2 = s2.Data;
        }

        // سداد مرتبط على الفاتورة الأولى + تحصيل عام (تسوية الأقدم أولًا)
        await using (var db = _fx.CreateContext())
        {
            var linked = await new clsPaymentService(db).PayInvoiceAsync(new DtoPaymentRequest
            { InvoiceId = sale1, PartyId = seed.CustomerId, Amount = 40m, PaymentDate = Today, Method = "cash", UserId = seed.UserId });
            Assert.True(linked.IsSuccess, linked.Message);
            var general = await new clsPaymentService(db).ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            { PartyId = seed.CustomerId, Amount = 60m, PaymentDate = Today, Method = "cash", UserId = seed.UserId });
            Assert.True(general.IsSuccess, general.Message);

            // الرصيد 150 − 40 − 60 = 50 بالدفتر؛ التسوية خصمت الفاتورة الأقدم لا الاثنتين.
            // بيع 50 = الحد المتبقي تمامًا (100) — نجاح بلا ازدواج عدّ.
            var r = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 50m, 0m));
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(100m, await BalanceOfAsync(db, seed.CustomerId));

            // مستحق فاتورة sale2 ما زال 50 عبر التسوية الحتمية (لم يُحتسب مرتين في الرصيد)
            var rows = await clsPaymentAllocation.GetPartyOutstandingAsync(db, 'C', seed.CustomerId);
            Assert.Equal(50m, rows.First(x => x.InvoiceId == sale2).Outstanding);
        }
    }

    // ══ 10) حد صفري: منع مطلق — رفض أعمال صريح لا مسار اعتماد له إطلاقًا ══
    [Fact]
    public async Task ZeroCreditLimit_CreditSale_IsHardRejection_NotApprovalRequired()
    {
        var seed = await SeedWithStockAsync($"cl10-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 0m);
        decimal inventoryBefore = await db.Inventories.AsNoTracking()
            .Where(v => v.ProductId == seed.ProductId).Select(v => v.QuantityAvailable ?? 0).SingleAsync();
        int invoicesBefore = await db.Invoices.CountAsync(i => i.UserId == seed.UserId && i.Type == enInvoicesType.sale);
        int movementsBefore = await db.StockMovements.CountAsync(m => m.ProductId == seed.ProductId && m.MovementType == enStockMovementType.sale);

        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 1m, 0m));

        // رفض أعمال عادي صريح — ليست نتيجة «يتطلب موافقة المدير»: الواجهة لا تفتح
        // نافذة الاعتماد لهذا الرفض أصلًا (التمييز آليًا بنوع النتيجة).
        Assert.False(r.IsSuccess);
        Assert.IsNotType<CreditApprovalRequiredResult<int>>(r);
        Assert.Contains("ممنوع", r.Message); // الشراء بالدين ممنوع — حد الائتمان صفر

        // لا سجل اعتماد أصلًا (المنع الصفري لا يُعتمد مهما كانت صلاحيات طالب البيع)
        Assert.Equal(0, await db.CreditApprovals.CountAsync(a => a.CustomerId == seed.CustomerId));

        // ولا أي أثر جزئي: لا فاتورة بيع ولا دفعات ولا حركات مخزون ولا دَين
        await AssertNoTraceAsync(db, seed, seed.UserId, inventoryBefore, balanceBefore: 0m, invoicesBefore, movementsBefore);
    }

    // ══ 10-ب) البيع النقدي الكلي لعميل الحد الصفري يبقى مسموحًا ══
    [Fact]
    public async Task ZeroCreditLimit_CashOnlySale_StillAllowed()
    {
        var seed = await SeedWithStockAsync($"cl10b-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 0m);

        var cash = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 1m, 1m));
        Assert.True(cash.IsSuccess, cash.Message);
        Assert.Equal(0m, await BalanceOfAsync(db, seed.CustomerId));
        Assert.Equal(0, await db.CreditApprovals.CountAsync(a => a.CustomerId == seed.CustomerId));
    }

    // ══ 10-ج) رفع الحد الدائم إلى موجب يعيد تفعيل تقييم السياسة العادي ══
    // (الطريق الوحيد لتمكين الائتمان: تعديل المدير للحد الدائم — لا اعتماد تجاوز)
    [Fact]
    public async Task ZeroLimitRaisedToPositive_NormalPolicyEvaluationResumes()
    {
        var seed = await SeedWithStockAsync($"cl10c-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 0m);

        // داخل المنع: أي آجل مرفوض رفضًا صريحًا
        var blocked = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 1m, 0m));
        Assert.False(blocked.IsSuccess);
        Assert.IsNotType<CreditApprovalRequiredResult<int>>(blocked);

        // المدير يعدّل الحد الدائم إلى 50 (خدمة تعديل الحد الحقيقية)
        bool raise = await new clsCustomerService(db).UpdateCustomerCreditLimitAsync(seed.CustomerId, 50m);
        Assert.True(raise);

        // بعدها: سياسة الحد الموجب المعتادة — ضمن الحد نجاح، وفوقه نتيجة اعتماد
        var within = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 30m, 0m));
        Assert.True(within.IsSuccess, within.Message);

        var over = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 30m, 0m));
        AssertApprovalRequired(over, seed.CustomerId, current: 30m, limit: 50m, newCredit: 30m);
    }

    // ══ 11) حد ائتماني سالب: بيانات فاسدة — رفض تحقق صريح لا اعتماد ══
    [Fact]
    public async Task NegativeCreditLimit_IsRejectedByUpdateAndCheckoutGuard()
    {
        var seed = await SeedWithStockAsync($"cl11-{Guid.NewGuid():N}"[..12]);

        // خدمة تعديل الحد ترفض السالب
        await using (var db = _fx.CreateContext())
        {
            await Assert.ThrowsAsync<ConflictException>(
                () => new clsCustomerService(db).UpdateCustomerCreditLimitAsync(seed.CustomerId, -5m));
        }

        // حارس البيع يرفض حدًا سالبًا رفضًا صريحًا (ليس نتيجة اعتماد)
        await using (var db = _fx.CreateContext())
        {
            await SetLimitAsync(db, seed.CustomerId, -5m);
            var r = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 10m, 0m));
            Assert.False(r.IsSuccess);
            Assert.IsNotType<CreditApprovalRequiredResult<int>>(r);
            Assert.Contains("سالب", r.Message);
        }
    }

    // ══ 12) بيع نقدي كلي بلا عميل: لا يمس السياسة أصلًا ══
    [Fact]
    public async Task CashOnlyCheckout_WithoutCustomer_Unaffected()
    {
        var seed = await SeedWithStockAsync($"cl12-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();

        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, null, 25m, 25m));
        Assert.True(r.IsSuccess, r.Message);
    }

    // ══ 13) دفعات مختلطة (نقدي + شبكة): الجزء الآجل وحده يخضع للحد ══
    [Fact]
    public async Task MixedPayments_OnlyCreditPortionSubjectToLimit()
    {
        var seed = await SeedWithStockAsync($"cl13-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 40m);

        // صافي 100: نقد 70 + شبكة 10 → آجل 20 ≤ 40 ينجح
        var mixed = new DtoCheckoutRequest
        {
            CustomerId = seed.CustomerId,
            UserId = seed.UserId,
            Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 1, UnitPrice = 100m } },
            Payments = new List<DtoPaymentInfo>
            {
                new() { Amount = 70m, Method = "cash", PaymentDate = Today },
                new() { Amount = 10m, Method = "card", PaymentDate = Today }
            }
        };
        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(mixed);
        Assert.True(r.IsSuccess, r.Message);
        Assert.Equal(20m, await BalanceOfAsync(db, seed.CustomerId));

        // نفس الطلب بلا الدفعتين: آجل 100 > 40 → اعتماد
        var creditOnly = SaleRequest(seed, seed.CustomerId, 100m, 0m);
        var over = await new clsCheckoutService(db).ProcessCheckoutAsync(creditOnly);
        AssertApprovalRequired(over, seed.CustomerId, current: 20m, limit: 40m, newCredit: 100m);
    }

    // ══ 14) إكمال فاتورة معلقة: نفس السياسة ══
    [Fact]
    public async Task SuspendedInvoiceCompletion_AppliesSamePolicy()
    {
        var seed = await SeedWithStockAsync($"cl14-{Guid.NewGuid():N}"[..12]);
        int suspendedId;
        await using (var db = _fx.CreateContext())
        {
            await SetLimitAsync(db, seed.CustomerId, 30m);
            var susp = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 50m, 0m), suspend: true);
            Assert.True(susp.IsSuccess, susp.Message); // التعليق لا يُقيّد دَينًا
            suspendedId = susp.Data;
        }

        // إكمال بلا سداد: آجل 50 > 30 → نتيجة اعتماد منظمة، والفاتورة تعود معلقة
        await using (var db = _fx.CreateContext())
        {
            var done = await new clsCheckoutService(db).CompleteSuspendedInvoiceAsync(
                suspendedId, new List<DtoPaymentInfo> { new() { Amount = 10m, Method = "cash" } }, seed.UserId);
            Assert.False(done.IsSuccess);
            var approval = Assert.IsType<CreditApprovalRequiredResult>(done);
            Assert.Equal(seed.CustomerId, approval.ApprovalInfo!.CustomerId);
            Assert.Equal(0m, approval.ApprovalInfo.CurrentOutstanding);
            Assert.Equal(40m, approval.ApprovalInfo.NewCreditAmount);
            Assert.Equal(enInvoicesStatus.suspended, await db.Invoices.AsNoTracking()
                .Where(i => i.InvoiceId == suspendedId).Select(i => i.Status).SingleAsync());

            // الإكمال النقدي الكلي ينجح
            var cash = await new clsCheckoutService(db).CompleteSuspendedInvoiceAsync(
                suspendedId, new List<DtoPaymentInfo> { new() { Amount = 50m, Method = "cash" } }, seed.UserId);
            Assert.True(cash.IsSuccess, cash.Message);
            Assert.Equal(0m, await BalanceOfAsync(db, seed.CustomerId));
        }
    }

    // ══ 18) المشروع = الرصيد الفعلي بعد نجاح البيع ══
    [Fact]
    public async Task ProjectedOutstanding_MatchesActualBalanceAfterSuccessfulCheckout()
    {
        var seed = await SeedWithStockAsync($"cl18-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 200m);

        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 120m, 20m));
        Assert.True(r.IsSuccess, r.Message);
        // الآجل 100 (120 − 20) قُيّد بالكامل — لا كسور ولا فروق
        Assert.Equal(100m, await BalanceOfAsync(db, seed.CustomerId));

        var rows = await clsPaymentAllocation.GetPartyOutstandingAsync(db, 'C', seed.CustomerId);
        Assert.Equal(100m, rows.Sum(x => x.Outstanding));
    }

    // ══ 19) الرفض لا يترك أثرًا جزئيًا (فحص شامل: دَين سابق + بيع مرفوض) ══
    [Fact]
    public async Task RejectedCheckout_LeavesNoPartialState_WithPriorDebt()
    {
        var seed = await SeedWithStockAsync($"cl19-{Guid.NewGuid():N}"[..12], qty: 100);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);

        var first = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 80m, 0m));
        Assert.True(first.IsSuccess, first.Message);
        decimal inventoryBefore = await db.Inventories.AsNoTracking()
            .Where(v => v.ProductId == seed.ProductId).Select(v => v.QuantityAvailable ?? 0).SingleAsync();
        decimal balanceBefore = await BalanceOfAsync(db, seed.CustomerId);
        int invoicesBefore = await db.Invoices.CountAsync(i => i.UserId == seed.UserId && i.Type == enInvoicesType.sale);
        int movementsBefore = await db.StockMovements.CountAsync(m => m.ProductId == seed.ProductId && m.MovementType == enStockMovementType.sale);

        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 50m, 0m));
        Assert.False(r.IsSuccess);
        await AssertNoTraceAsync(db, seed, seed.UserId, inventoryBefore, balanceBefore, invoicesBefore, movementsBefore);
    }

    // ══ 20 + 22) بيعان متزامنان لا يتجاوزان الحد معًا — ولا فاتورة مكررة ══
    [Fact]
    public async Task TwoConcurrentCreditSales_CannotSilentlyExceedLimit()
    {
        var seed = await SeedWithStockAsync($"cl20-{Guid.NewGuid():N}"[..12], qty: 10);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);

        // كل بيع بمفرده داخل الحد (60) — معًا (120) يتجاوزه: واحد فقط يلتزم.
        // سياق مستقل لكل مهمة (DbContext ليس آمنًا خيطيًا) كنمط اختبارات التزامن القائمة.
        var t1 = Task.Run(async () =>
        {
            await using var c = _fx.CreateContext();
            return await new clsCheckoutService(c).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 60m, 0m));
        });
        var t2 = Task.Run(async () =>
        {
            await using var c = _fx.CreateContext();
            return await new clsCheckoutService(c).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 60m, 0m));
        });
        var results = await Task.WhenAll(t1, t2);

        int successes = results.Count(r => r.IsSuccess);
        int approvals = results.Count(r => r is CreditApprovalRequiredResult<int> { IsSuccess: false });
        Assert.Equal(1, successes);
        Assert.Equal(1, approvals);

        // لا دَين فوق الحد ولا فاتورة مكررة (إعادة المحاولة لا تكرر القيد)
        decimal finalBalance = await BalanceOfAsync(db, seed.CustomerId);
        Assert.True(finalBalance <= 100m, $"الرصيد تجاوز الحد: {finalBalance}");
        Assert.Equal(1, await db.Invoices.CountAsync(i => i.PartyId == seed.CustomerId && i.PartyType == 'C' && i.Type == enInvoicesType.sale));
    }

    // ══ 21) التراجع الصحيح: رفض بعد خصم المخزون يعيد كل شيء ══
    [Fact]
    public async Task RejectedSale_RollsBackStockAndMovements_Completely()
    {
        var seed = await SeedWithStockAsync($"cl21-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 0m);
        decimal inventoryBefore = await db.Inventories.AsNoTracking()
            .Where(v => v.ProductId == seed.ProductId).Select(v => v.QuantityAvailable ?? 0).SingleAsync();
        int batchesBefore = await db.Batches.CountAsync(b => b.ProductId == seed.ProductId);

        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 10m, 0m));
        Assert.False(r.IsSuccess);

        Assert.Equal(inventoryBefore, await db.Inventories.AsNoTracking()
            .Where(v => v.ProductId == seed.ProductId).Select(v => v.QuantityAvailable ?? 0).SingleAsync());
        Assert.Equal(batchesBefore, await db.Batches.CountAsync(b => b.ProductId == seed.ProductId));
        Assert.Equal(0, await db.InvoiceItems.CountAsync(ii => ii.Invoice.Type == enInvoicesType.sale && ii.Invoice.PartyId == seed.CustomerId));
    }
}
