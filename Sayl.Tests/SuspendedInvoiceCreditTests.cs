using Common.DTOs;
using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// اختبارات سير اعتماد المدير عند إكمال الفواتير المعلقة (Task 4 — البنود 18-25).
/// نقطة الإنفاذ نفسها: CompleteSuspendedInvoiceAsync يمر بـ ProcessPaymentAsync
/// (القمع الوحيد لسياسة الحد) — تجاوز الحد الموجب يُعيد نتيجة منظمة
/// CreditApprovalRequiredResult والفاتورة تعود معلقة بلا أي أثر (المعاملة
/// تراجعت)، ومنح الاعتماد ثم إعادة الإكمال بمعرفه يستهلكه مرة واحدة بالضبط
/// داخل معاملة الإكمال (CAS انتقال الحالة + ربط صارم).
/// المنع الصفري: حد صفر = منع مطلق — رفض أعمال عادي لا نتيجة اعتماد،
/// ولا نافذة ولا سجل CreditApprovals ممكن، والفاتورة تبقى معلقة سليمة.
/// </summary>
[Collection("database")]
public class SuspendedInvoiceCreditTests
{
    private readonly DatabaseFixture _fx;
    public SuspendedInvoiceCreditTests(DatabaseFixture fx) => _fx = fx;

    private const string ManagerPassword = "Manager@123";
    private const string Reason = "إكمال فاتورة معلقة متجاوزة للحد — تفويض استثنائي لمرة واحدة";

    /// <summary>بذرة + مخزون، ثم تعليق فاتورة آجلة للعميل (لا دَين عند التعليق).
    /// يُستخدم سياق جديد للشراء (داخلي) ثم للتعليق (داخلي) حتى لا تحمل
    /// كيانات Inventory المتتبعة حالتاً قديمة من التحديث الذري الخام في
    /// PurchaseService (مشكلة EF Core المعروفة: الكيان المتتبع لا يُحدَّث
    /// من Raw SQL UPDATE، والتعليق يعتمد على الكمية المتاحة من المخزون).</summary>
    private async Task<(TestSeed Seed, int SuspendedId)> SuspendInvoiceAsync(
        string marker, int qty = 10, decimal unitPrice = 50m)
    {
        // شراء: سياق منفصل ثم يُتخلص منه قبل التعليق
        int userId, supplierId, productId, customerId, suspendedId;
        {
            await using var db = _fx.CreateContext();
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId;
            supplierId = seed.SupplierId;
            productId = seed.ProductId;
            customerId = seed.CustomerId;

            var r = await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
            {
                SupplierId = supplierId,
                UserId = userId,
                Items = { new DtoPurchaseItem { ProductId = productId, BatchNumber = $"B-{marker}", Quantity = qty, UnitCost = 5m } }
            });
            Assert.True(r.IsSuccess, r.Message);
        } // سياق الشراء يُتخلص منه — كيانات Inventory المتتبعة لا تنتقل

        // تعليق: سياق جديد نظيف يقرأ الكميات الحية من القاعدة
        {
            await using var db = _fx.CreateContext();
            var susp = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                CustomerId = customerId,
                UserId = userId,
                Items = { new DtoCheckoutItem { ProductId = productId, ProductName = "x", Quantity = 1, UnitPrice = unitPrice } },
                Payment = new DtoPaymentInfo { Amount = 0m, Method = "cash" }
            }, suspend: true);
            Assert.True(susp.IsSuccess, susp.Message);
            suspendedId = susp.Data;
        }

        return (new TestSeed { UserId = userId, SupplierId = supplierId, ProductId = productId, CustomerId = customerId }, suspendedId);
    }

    /// <summary>مستخدم اختبار حقيقي بكلمة مرور BCrypt صالحة (للمصادقة عبر LoginAsync).</summary>
    private async Task<(int UserId, string Username)> CreateManagerAsync(SaylDbContext db, string marker)
    {
        var role = await TestSeed.GetOrCreateRoleAsync(db, AppPermissions.AdminRoleValue);
        string username = $"si-{marker}-{Guid.NewGuid():N}";
        var user = new User
        {
            Username = username.Length > 50 ? username[..50] : username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(ManagerPassword),
            Role = role,
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user.UserId, user.Username);
    }

    private static clsCreditApprovalService ApprovalService(SaylDbContext db)
        => new(db, new clsUsersService(db));

    private static async Task SetLimitAsync(SaylDbContext db, int customerId, decimal limit)
        => await db.Customers.Where(c => c.PartyId == customerId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.CreditLimit, limit));

    private async Task<decimal> BalanceOfAsync(SaylDbContext db, int customerId)
        => await db.Customers.AsNoTracking().Where(c => c.PartyId == customerId)
            .Select(c => c.Balance ?? 0m).SingleAsync();

    /// <summary>فواتير معلقة تُستكمل بآجل فوق الحد: نتيجة اعتماد منظمة.
    /// (بلا معرف اعتماد: رفض منظوم — نافذة المدير في الواجهة.)</summary>
    private async Task<CreditApprovalRequiredResult> CompleteRequiringApprovalAsync(
        SaylDbContext db, int suspendedId, TestSeed seed, decimal paid)
    {
        var done = await new clsCheckoutService(db).CompleteSuspendedInvoiceAsync(
            suspendedId, new List<DtoPaymentInfo> { new() { Amount = paid, Method = "cash" } }, seed.UserId);
        Assert.False(done.IsSuccess);
        var approval = Assert.IsType<CreditApprovalRequiredResult>(done);
        Assert.NotNull(approval.ApprovalInfo);
        return approval;
    }

    // ══ 18) إكمال داخل الحد الموجب: نجاح عادي بلا اعتماد ══
    [Fact]
    public async Task Completion_WithinPositiveLimit_SucceedsNormally()
    {
        var (seed, suspendedId) = await SuspendInvoiceAsync($"si18-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);

        // إكمال بسداد جزئي (10) — الآجل 40 داخل الحد 100: نجاح عادي بلا اعتماد
        var done = await new clsCheckoutService(db).CompleteSuspendedInvoiceAsync(
            suspendedId, new List<DtoPaymentInfo> { new() { Amount = 10m, Method = "cash" } }, seed.UserId);

        Assert.True(done.IsSuccess, done.Message);
        Assert.IsNotType<CreditApprovalRequiredResult>(done);
        Assert.Equal(40m, await BalanceOfAsync(db, seed.CustomerId));
        Assert.Equal(enInvoicesStatus.completed, await db.Invoices.AsNoTracking()
            .Where(i => i.InvoiceId == suspendedId).Select(i => i.Status).SingleAsync());
        Assert.Equal(0, await db.CreditApprovals.CountAsync(a => a.CustomerId == seed.CustomerId));
    }

    // ══ 19 + 23) إكمال متجاوز للحد الموجب: نتيجة اعتماد والفاتورة تعود معلقة سليمة ══
    [Fact]
    public async Task Completion_ExceedingPositiveLimit_ReturnsApprovalRequired_InvoiceStaysIntact()
    {
        var (seed, suspendedId) = await SuspendInvoiceAsync($"si19-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 30m);

        int reservedBefore = await db.StockReservations
            .Where(r => r.InvoiceId == suspendedId).SumAsync(r => r.Quantity);
        int invReservedBefore = await db.Inventories.AsNoTracking()
            .Where(v => v.ProductId == seed.ProductId).Select(v => v.QuantityReserved ?? 0).SingleAsync();
        int paymentsBefore = await db.Payments.CountAsync(p => p.InvoiceId == suspendedId);
        int movementsBefore = await db.StockMovements.CountAsync(m => m.SourceId == suspendedId && m.SourceType == "Invoice");

        var done = await new clsCheckoutService(db).CompleteSuspendedInvoiceAsync(
            suspendedId, new List<DtoPaymentInfo> { new() { Amount = 10m, Method = "cash" } }, seed.UserId);

        // نتيجة اعتماد منظمة بحقول مطابقة (آجل 40 فوق حد 30)
        Assert.False(done.IsSuccess);
        var approval = Assert.IsType<CreditApprovalRequiredResult>(done);
        Assert.NotNull(approval.ApprovalInfo);
        Assert.Equal(seed.CustomerId, approval.ApprovalInfo!.CustomerId);
        Assert.Equal(0m, approval.ApprovalInfo.CurrentOutstanding);
        Assert.Equal(30m, approval.ApprovalInfo.CreditLimit);
        Assert.Equal(40m, approval.ApprovalInfo.NewCreditAmount);
        Assert.Equal(40m, approval.ApprovalInfo.ProjectedOutstanding);
        Assert.Equal(10m, approval.ApprovalInfo.ExcessAmount);

        // الفاتورة تعود معلقة سليمة: الحجز وحالة المخزون والدفعات والحركات كما كانت
        // AsNoTracking: استبعاد الكيانات المتتبعة من عملية الفحص (التغيير المتتبعة لا يغيّر حالة القاعدة)
        Assert.Equal(enInvoicesStatus.suspended, await db.Invoices.AsNoTracking()
            .Where(i => i.InvoiceId == suspendedId).Select(i => i.Status).SingleAsync());
        Assert.Equal(reservedBefore, await db.StockReservations
            .Where(r => r.InvoiceId == suspendedId).SumAsync(r => r.Quantity));
        Assert.Equal(invReservedBefore, await db.Inventories.AsNoTracking()
            .Where(v => v.ProductId == seed.ProductId).Select(v => v.QuantityReserved ?? 0).SingleAsync());
        Assert.Equal(paymentsBefore, await db.Payments.CountAsync(p => p.InvoiceId == suspendedId));
        Assert.Equal(movementsBefore, await db.StockMovements.CountAsync(m => m.SourceId == suspendedId && m.SourceType == "Invoice"));
        Assert.Equal(0m, await BalanceOfAsync(db, seed.CustomerId));
    }

    // ══ 20) اعتماد صالح يسمح بإكمال نفس الفاتورة المعلقة بمعرفه ══
    [Fact]
    public async Task ValidApproval_AllowsCompletingSameSuspendedInvoice()
    {
        var (seed, suspendedId) = await SuspendInvoiceAsync($"si20-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 30m);

        var approvalRequired = await CompleteRequiringApprovalAsync(db, suspendedId, seed, paid: 10m);
        var manager = await CreateManagerAsync(db, "si20");

        var grant = await ApprovalService(db).ApproveCreditSaleAsync(
            approvalRequired.ApprovalInfo!, manager.Username, ManagerPassword, Reason);
        Assert.True(grant.IsSuccess, grant.Message);

        // إعادة الإكمال نفسه بمعرف الاعتماد — الاستهلاك داخل معاملة الإكمال
        var retry = await new clsCheckoutService(db).CompleteSuspendedInvoiceAsync(
            suspendedId, new List<DtoPaymentInfo> { new() { Amount = 10m, Method = "cash" } },
            seed.UserId, creditApprovalId: grant.Data);

        Assert.True(retry.IsSuccess, retry.Message);
        Assert.Equal(40m, await BalanceOfAsync(db, seed.CustomerId));
        // AsNoTracking: استبعاد الكيانات المتتبعة من فحص الحالة
        Assert.Equal(enInvoicesStatus.completed, await db.Invoices.AsNoTracking()
            .Where(i => i.InvoiceId == suspendedId).Select(i => i.Status).SingleAsync());
        Assert.Equal(0, await db.StockReservations.CountAsync(r => r.InvoiceId == suspendedId));
    }

    // ══ 21) الاعتماد يُستهلك مرة واحدة بالضبط في سير الإكمال ══
    [Fact]
    public async Task ApprovalConsumed_ExactlyOnce_InSuspendedCompletion()
    {
        // فاتورتان معلقتان متجاوزتان للحد — اعتماد واحد لا يكفي إلا لأولاهما
        var (seed, inv1) = await SuspendInvoiceAsync($"si21a-{Guid.NewGuid():N}"[..12]);
        int inv2;
        await using (var db = _fx.CreateContext())
        {
            var s2 = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                CustomerId = seed.CustomerId,
                UserId = seed.UserId,
                Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 1, UnitPrice = 50m } },
                Payment = new DtoPaymentInfo { Amount = 0m, Method = "cash" }
            }, suspend: true);
            Assert.True(s2.IsSuccess, s2.Message);
            inv2 = s2.Data;
            await SetLimitAsync(db, seed.CustomerId, 30m);
        }

        await using var db2 = _fx.CreateContext();
        var manager = await CreateManagerAsync(db2, "si21");
        var first = await CompleteRequiringApprovalAsync(db2, inv1, seed, paid: 10m);
        var grant = await ApprovalService(db2).ApproveCreditSaleAsync(
            first.ApprovalInfo!, manager.Username, ManagerPassword, Reason);
        Assert.True(grant.IsSuccess, grant.Message);

        var retry1 = await new clsCheckoutService(db2).CompleteSuspendedInvoiceAsync(
            inv1, new List<DtoPaymentInfo> { new() { Amount = 10m, Method = "cash" } },
            seed.UserId, creditApprovalId: grant.Data);
        Assert.True(retry1.IsSuccess, retry1.Message);

        // صف الاعتماد: مستهلك بفاتورة الإكمال الأولى وحدها
        var row = await db2.CreditApprovals.AsNoTracking().SingleAsync(a => a.ApprovalId == grant.Data);
        Assert.Equal("consumed", row.Status);
        Assert.Equal(inv1, row.ConsumedByInvoiceId);
        Assert.NotNull(row.ConsumedAtUtc);

        // إعادة استخدامه لإكمال الفاتورة الثانية: مرفوض — اعتماد جديد مطلوب
        var reuse = await new clsCheckoutService(db2).CompleteSuspendedInvoiceAsync(
            inv2, new List<DtoPaymentInfo> { new() { Amount = 10m, Method = "cash" } },
            seed.UserId, creditApprovalId: grant.Data);
        Assert.False(reuse.IsSuccess);
        Assert.IsType<CreditApprovalRequiredResult>(reuse);
        Assert.Contains("لم يعد صالحًا", reuse.Message);
        Assert.Equal(enInvoicesStatus.suspended, await db2.Invoices.AsNoTracking()
            .Where(i => i.InvoiceId == inv2).Select(i => i.Status).SingleAsync());

        // الحرق بعد الاستهلاك لا يعيد الحالة granted (استهلاك حقيقي لا يُمس)
        Assert.Equal("consumed", (await db2.CreditApprovals.AsNoTracking()
            .SingleAsync(a => a.ApprovalId == grant.Data)).Status);
    }

    // ══ 22) حد صفري: الإكمال الآجل ممنوع حتى بأوراق اعتماد مدير حقيقية ══
    [Fact]
    public async Task ZeroLimitSuspendedInvoice_CannotBeCompletedAsCredit_EvenWithManagerCredentials()
    {
        var (seed, suspendedId) = await SuspendInvoiceAsync($"si22-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 0m);
        var manager = await CreateManagerAsync(db, "si22");

        // الإكمال بآجل (سداد جزئي 10 — آجل 40): رفض أعمال صريح — لا نتيجة اعتماد
        // (لا نافذة مدير أصلًا). دفعة صفرية تُرفض فحص الدفعات قبل سياسة الحد.
        var done = await new clsCheckoutService(db).CompleteSuspendedInvoiceAsync(
            suspendedId, new List<DtoPaymentInfo> { new() { Amount = 10m, Method = "cash" } }, seed.UserId);
        Assert.False(done.IsSuccess);
        Assert.IsNotType<CreditApprovalRequiredResult>(done);
        Assert.Contains("ممنوع", done.Message);

        // ومحاولة منح اعتماد بأوراق مدير حقيقية: مرفوض — لا سجل ممكن
        var grant = await ApprovalService(db).ApproveCreditSaleAsync(new CreditApprovalRequiredInfo
        {
            CustomerId = seed.CustomerId, CurrentOutstanding = 0m, CreditLimit = 0m,
            NewCreditAmount = 50m, ProjectedOutstanding = 50m, ExcessAmount = 50m
        }, manager.Username, ManagerPassword, Reason);
        Assert.False(grant.IsSuccess);
        Assert.Equal(0, await db.CreditApprovals.CountAsync(a => a.CustomerId == seed.CustomerId));

        // الفاتورة بقيت معلقة سليمة وقابلة للاستكمال النقدي
        Assert.Equal(enInvoicesStatus.suspended, await db.Invoices.AsNoTracking()
            .Where(i => i.InvoiceId == suspendedId).Select(i => i.Status).SingleAsync());
        var cash = await new clsCheckoutService(db).CompleteSuspendedInvoiceAsync(
            suspendedId, new List<DtoPaymentInfo> { new() { Amount = 50m, Method = "cash" } }, seed.UserId);
        Assert.True(cash.IsSuccess, cash.Message);
        Assert.Equal(0m, await BalanceOfAsync(db, seed.CustomerId));
    }

    // ══ 24) الرفض لا يكرر شيئًا: إكمال تالٍ ينجح مرة واحدة بدفعة واحدة ══
    [Fact]
    public async Task RejectedCompletion_DoesNotDuplicatePaymentsMovementsDebtOrReservations()
    {
        var (seed, suspendedId) = await SuspendInvoiceAsync($"si24-{Guid.NewGuid():N}"[..12]);
        await using var db0 = _fx.CreateContext();
        await SetLimitAsync(db0, seed.CustomerId, 30m);

        // رفض أول (نتيجة اعتماد) ثم رفض ثانٍ (نفس الحالة) — لا تراكم أثر
        await CompleteRequiringApprovalAsync(db0, suspendedId, seed, paid: 10m);
        await CompleteRequiringApprovalAsync(db0, suspendedId, seed, paid: 10m);

        await using var db = _fx.CreateContext();

        // رفع الحد الدائم ثم الإكمال النهائي: نجاح مرة واحدة بدفعة واحدة
        bool raise = await new clsCustomerService(db).UpdateCustomerCreditLimitAsync(seed.CustomerId, 100m);
        Assert.True(raise);
        var done = await new clsCheckoutService(db).CompleteSuspendedInvoiceAsync(
            suspendedId, new List<DtoPaymentInfo> { new() { Amount = 10m, Method = "cash" } }, seed.UserId);
        Assert.True(done.IsSuccess, done.Message);

        // دفعة واحدة فقط (لا دفعات الرفضين تراكمت) وحركة بيع واحدة لكل دفعة استُهلكت
        Assert.Equal(1, await db.Payments.CountAsync(p => p.InvoiceId == suspendedId));
        int movements = await db.StockMovements.CountAsync(m => m.SourceId == suspendedId && m.SourceType == "Invoice");
        int items = await db.InvoiceItems.CountAsync(ii => ii.InvoiceId == suspendedId);
        Assert.Equal(items, movements); // صف حركة لكل صف بند — لا ازدواج
        Assert.Equal(40m, await BalanceOfAsync(db, seed.CustomerId)); // 50 − 10 مرة واحدة
        Assert.Equal(0, await db.StockReservations.CountAsync(r => r.InvoiceId == suspendedId));
    }

    // ══ 25) فشل الإكمال بعد المنح يحرق الاعتماد ويحفظ أثر التدقيق ══
    [Fact]
    public async Task FailedPostApprovalCompletion_BurnsApproval_WithAuditRecord()
    {
        var (seed, suspendedId) = await SuspendInvoiceAsync($"si25-{Guid.NewGuid():N}"[..12], qty: 1);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 30m);
        var manager = await CreateManagerAsync(db, "si25");

        var approvalRequired = await CompleteRequiringApprovalAsync(db, suspendedId, seed, paid: 10m);
        var grant = await ApprovalService(db).ApproveCreditSaleAsync(
            approvalRequired.ApprovalInfo!, manager.Username, ManagerPassword, Reason);
        Assert.True(grant.IsSuccess, grant.Message);

        // انحراف خارجي: المخزون الفعلي يختفي بعد المنح (نمط تشخيص VULN-20) —
        // الإكمال التالي يفشل تحقق مخزون فتحترق الاعتماد (سياسة الفشل)
        await db.Inventories.Where(v => v.ProductId == seed.ProductId)
            .ExecuteUpdateAsync(u => u.SetProperty(v => v.QuantityAvailable, 0));

        var retry = await new clsCheckoutService(db).CompleteSuspendedInvoiceAsync(
            suspendedId, new List<DtoPaymentInfo> { new() { Amount = 10m, Method = "cash" } },
            seed.UserId, creditApprovalId: grant.Data);

        Assert.False(retry.IsSuccess);
        Assert.IsNotType<CreditApprovalRequiredResult>(retry); // فشل عام لا طلب اعتماد جديد

        // الحرق: حالة صريحة بلا استهلاك + سجل تدقيق مرتبط بالاعتماد
        var row = await db.CreditApprovals.AsNoTracking().SingleAsync(a => a.ApprovalId == grant.Data);
        Assert.Equal("burned", row.Status);
        Assert.Null(row.ConsumedByInvoiceId);
        Assert.Null(row.ConsumedAtUtc);
        Assert.Equal(Reason, row.Reason);
        Assert.Equal(manager.UserId, row.ManagerUserId);
        Assert.Equal(1, await db.SystemLogs.CountAsync(l =>
            l.Action == "CreditApprovalBurned" && l.Details!.Contains($"#{grant.Data} ")));

        // الفاتورة بقيت معلقة سليمة وحجزها لم يُمس (المعاملة تراجعت)
        Assert.Equal(enInvoicesStatus.suspended, await db.Invoices.AsNoTracking()
            .Where(i => i.InvoiceId == suspendedId).Select(i => i.Status).SingleAsync());
        Assert.Equal(1, await db.StockReservations.CountAsync(r => r.InvoiceId == suspendedId));
        Assert.Equal(0m, await BalanceOfAsync(db, seed.CustomerId));
    }
}
