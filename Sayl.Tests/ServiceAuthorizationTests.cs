using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// Service-level authorization gates for returns and payments (UI is display-only):
///   - Customer return: cashier identity OR SalesInvoices bit (16).
///   - Supplier return: PurchaseInvoices bit (32).
///   - Customer collection: Customers bit (4). Supplier payment: Suppliers bit (8).
///   - Invoice payment: SalesInvoices/PurchaseInvoices bit by invoice type.
///   - Supplied session must exist, be open, and be owned by the actor (or admin).
///   - SessionId = null never bypasses permission checks; it remains legitimate
///     for operations that run outside a drawer (existing compat/background rule).
/// Fresh context per step (same pattern as SessionAttributionTests).
/// </summary>
[Collection("database")]
public class ServiceAuthorizationTests
{
    private readonly DatabaseFixture _fx;
    public ServiceAuthorizationTests(DatabaseFixture fx) => _fx = fx;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    private static async Task<int> SeedUserAsync(SaylDbContext db, string marker, int roleValue)
    {
        var role = new Role { Name = $"role-{marker}", RoleValue = roleValue, IsActive = true };
        db.Roles.Add(role);
        var user = new User { Username = $"user-{marker}", PasswordHash = "x", Role = role, IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    private static async Task<int> OpenSessionAsync(SaylDbContext db, int userId, decimal opening)
    {
        var r = await new clsPosSessionService(db).OpenSessionAsync(userId, opening);
        Assert.True(r.IsSuccess, r.Message);
        return r.Data;
    }

    private static async Task<int> PurchaseAsync(SaylDbContext db, TestSeed seed, int userId,
        int qty = 10, decimal unitCost = 10m, decimal cashPaid = 0m)
    {
        var r = await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = userId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = $"B-{Guid.NewGuid():N}"[..12], Quantity = qty, UnitCost = unitCost } },
            Payment = new DtoPaymentInfo { Amount = cashPaid, Method = "cash", PaymentDate = Today }
        });
        Assert.True(r.IsSuccess, r.Message);
        return r.Data;
    }

    private static async Task<int> CreditSaleAsync(SaylDbContext db, TestSeed seed, int? sessionId, int qty, decimal unitPrice, decimal paid = 0m)
    {
        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            CustomerId = seed.CustomerId,
            UserId = seed.UserId,
            SessionId = sessionId,
            Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = qty, UnitPrice = unitPrice } },
            Payment = new DtoPaymentInfo { Amount = paid, Method = "cash", PaymentDate = Today }
        });
        Assert.True(r.IsSuccess, r.Message);
        return r.Data;
    }

    private static DtoReturnRequest ReturnRequest(int invoiceId, int userId, int? sessionId, int productId, int qty)
        => new()
        {
            OriginalInvoiceId = invoiceId,
            UserId = userId,
            SessionId = sessionId,
            Items = { new DtoReturnItem { ProductId = productId, Quantity = qty } }
        };

    private static DtoPaymentRequest PayRequest(int? invoiceId, int? partyId, decimal amount, int userId, int? sessionId)
        => new()
        {
            InvoiceId = invoiceId,
            PartyId = partyId,
            Amount = amount,
            PaymentDate = Today,
            Method = "cash",
            SessionId = sessionId,
            UserId = userId
        };

    private static async Task SetCustomerBalanceAsync(SaylDbContext db, int customerId, decimal balance)
        => await db.Customers.Where(c => c.PartyId == customerId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, balance));

    private static async Task SetSupplierBalanceAsync(SaylDbContext db, int supplierId, decimal balance)
        => await db.Suppliers.Where(s => s.PartyId == supplierId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Balance, balance));

    // ══ 1) مرتجع البيع: مستخدم مصرّح (مدير) بجلسة مفتوحة صالحة ══
    [Fact]
    public async Task CustomerReturn_AdminWithOpenSession_Succeeds()
    {
        string marker = $"au1-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int sessionId, saleId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, marker);
            sessionId = await OpenSessionAsync(db, seed.UserId, 0m);
            await PurchaseAsync(db, seed, seed.UserId);
        }

        await using (var db = _fx.CreateContext())
            saleId = await CreditSaleAsync(db, seed, sessionId, 5, 20m, paid: 100m);

        await using (var db = _fx.CreateContext())
        {
            var ret = await new clsReturnService(db)
                .CreateCustomerReturnAsync(ReturnRequest(saleId, seed.UserId, sessionId, seed.ProductId, 2));
            Assert.True(ret.IsSuccess, ret.Message);

            // المرتجع واسترداده النقدي يُنسبان لدرج الجلسة المفتوحة نفسها
            var returnInvoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceId == ret.Data);
            Assert.Equal(sessionId, returnInvoice.SessionId);
            var refund = await db.Payments.AsNoTracking()
                .SingleAsync(p => p.InvoiceId == ret.Data && p.Type == enPaymentType.outcome);
            Assert.Equal(sessionId, refund.SessionId);
        }
    }

    // ══ 2) مرتجع البيع: مستخدم بلا صلاحية — حتى بـ SessionId = null ══
    // (محاولة تجاوز الواجهة باستدعاء الخدمة مباشرة: البوابة في الخدمة)
    [Fact]
    public async Task CustomerReturn_UnauthorizedRole_Denied_EvenWithNullSession()
    {
        string marker = $"au2-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int saleId, outsiderId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, marker);
            outsiderId = await SeedUserAsync(db, marker + "-out", AppPermissions.Products); // بلا كاشير ولا بت 16
            await PurchaseAsync(db, seed, seed.UserId);
        }

        await using (var db = _fx.CreateContext())
            saleId = await CreditSaleAsync(db, seed, null, 5, 20m);

        await using (var db = _fx.CreateContext())
        {
            var ret = await new clsReturnService(db)
                .CreateCustomerReturnAsync(ReturnRequest(saleId, outsiderId, null, seed.ProductId, 2));
            Assert.False(ret.IsSuccess);
            Assert.Contains("صلاحية", ret.Message);
        }
    }

    // ══ 3) مرتجع البيع: الكاشير يحتفظ بحقه في نقطة البيع (سلوك قائم لا يُضعف) ══
    [Fact]
    public async Task CustomerReturn_Cashier_Succeeds()
    {
        string marker = $"au3-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int cashierId, sessionId, saleId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, marker);
            cashierId = await SeedUserAsync(db, marker + "-c", AppPermissions.CashierRoleValue);
            sessionId = await OpenSessionAsync(db, cashierId, 0m);
            await PurchaseAsync(db, seed, seed.UserId);
        }

        await using (var db = _fx.CreateContext())
            saleId = await CreditSaleAsync(db, seed, null, 5, 20m);

        await using (var db = _fx.CreateContext())
        {
            var ret = await new clsReturnService(db)
                .CreateCustomerReturnAsync(ReturnRequest(saleId, cashierId, sessionId, seed.ProductId, 1));
            Assert.True(ret.IsSuccess, ret.Message);
        }
    }

    // ══ 4) مرتجع بلا جلسة درج — مشروع (عملية خارج الدرج / توافق) ══
    [Fact]
    public async Task CustomerReturn_NullSession_Succeeds()
    {
        string marker = $"au4-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int saleId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, marker);
            await PurchaseAsync(db, seed, seed.UserId);
        }

        await using (var db = _fx.CreateContext())
            saleId = await CreditSaleAsync(db, seed, null, 5, 20m, paid: 100m);

        await using (var db = _fx.CreateContext())
        {
            var ret = await new clsReturnService(db)
                .CreateCustomerReturnAsync(ReturnRequest(saleId, seed.UserId, null, seed.ProductId, 1));
            Assert.True(ret.IsSuccess, ret.Message);

            var refund = await db.Payments.AsNoTracking()
                .SingleAsync(p => p.InvoiceId == ret.Data && p.Type == enPaymentType.outcome);
            Assert.Null(refund.SessionId);
        }
    }

    // ══ 5) مرتجع على جلسة مغلقة / غير موجودة يُرفض ══
    [Fact]
    public async Task CustomerReturn_ClosedOrMissingSession_Denied()
    {
        string marker = $"au5-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int sessionId, saleId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, marker);
            sessionId = await OpenSessionAsync(db, seed.UserId, 0m);
            await PurchaseAsync(db, seed, seed.UserId);
        }

        await using (var db = _fx.CreateContext())
            saleId = await CreditSaleAsync(db, seed, sessionId, 5, 20m);

        await using (var db = _fx.CreateContext())
        {
            var closed = await new clsPosSessionService(db).CloseSessionAsync(sessionId, 0m, actorUserId: seed.UserId);
            Assert.True(closed.IsSuccess, closed.Message);
        }

        await using (var db = _fx.CreateContext())
        {
            var returns = new clsReturnService(db);

            var onClosed = await returns
                .CreateCustomerReturnAsync(ReturnRequest(saleId, seed.UserId, sessionId, seed.ProductId, 1));
            Assert.False(onClosed.IsSuccess);
            Assert.Contains("مغلقة", onClosed.Message);

            var onMissing = await returns
                .CreateCustomerReturnAsync(ReturnRequest(saleId, seed.UserId, 999999, seed.ProductId, 1));
            Assert.False(onMissing.IsSuccess);
        }
    }

    // ══ 6) جلسة مستخدم آخر: يُرفض على غير المدير ويُسمح للمدير ══
    [Fact]
    public async Task CustomerReturn_ForeignSession_AdminAllowed_NonAdminDenied()
    {
        string marker = $"au6-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int cashierId, sessionId, saleId, clerkId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, marker); // مدير
            cashierId = await SeedUserAsync(db, marker + "-c", AppPermissions.CashierRoleValue);
            clerkId = await SeedUserAsync(db, marker + "-k", AppPermissions.SalesInvoices); // بت 16 بلا إدارة
            sessionId = await OpenSessionAsync(db, cashierId, 0m);
            await PurchaseAsync(db, seed, seed.UserId);
        }

        await using (var db = _fx.CreateContext())
            saleId = await CreditSaleAsync(db, seed, null, 5, 20m);

        await using (var db = _fx.CreateContext())
        {
            var denied = await new clsReturnService(db)
                .CreateCustomerReturnAsync(ReturnRequest(saleId, clerkId, sessionId, seed.ProductId, 1));
            Assert.False(denied.IsSuccess);
            Assert.Contains("مستخدم آخر", denied.Message);
        }

        await using (var db = _fx.CreateContext())
        {
            var byAdmin = await new clsReturnService(db)
                .CreateCustomerReturnAsync(ReturnRequest(saleId, seed.UserId, sessionId, seed.ProductId, 1));
            Assert.True(byAdmin.IsSuccess, byAdmin.Message);
        }
    }

    // ══ 7) مرتجع الشراء: يتطلب بت فواتير الشراء (32) — صلاحية البيع لا تكفي ══
    [Fact]
    public async Task SupplierReturn_RequiresPurchaseInvoicesBit()
    {
        string marker = $"au7-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int purchaseId, clerkId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, marker);
            clerkId = await SeedUserAsync(db, marker + "-k", AppPermissions.SalesInvoices); // بت 16 فقط
            purchaseId = await PurchaseAsync(db, seed, seed.UserId);
        }

        await using (var db = _fx.CreateContext())
        {
            var denied = await new clsReturnService(db)
                .CreateSupplierReturnAsync(ReturnRequest(purchaseId, clerkId, null, seed.ProductId, 2));
            Assert.False(denied.IsSuccess);
            Assert.Contains("صلاحية", denied.Message);
        }

        // المدير (يملك كل البتات) ينفذ بنجاح — بلا جلسة درج (مشروع خارج الدرج)
        await using (var db = _fx.CreateContext())
        {
            var ok = await new clsReturnService(db)
                .CreateSupplierReturnAsync(ReturnRequest(purchaseId, seed.UserId, null, seed.ProductId, 2));
            Assert.True(ok.IsSuccess, ok.Message);
        }
    }

    // ══ 8) التحصيل من عميل: بت شاشة العملاء (4) — والفحص لا يتجاوز بـ SessionId = null ══
    [Fact]
    public async Task CollectCustomerPayment_RequiresCustomersBit()
    {
        string marker = $"au8-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int collectorId, clerkId, debtorId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, marker);
            collectorId = await SeedUserAsync(db, marker + "-col", AppPermissions.Customers);   // بت 4 فقط
            clerkId = await SeedUserAsync(db, marker + "-clk", AppPermissions.SalesInvoices);   // بت 16 فقط
            debtorId = await SeedUserAsync(db, marker + "-bad", AppPermissions.Products);       // بلا أي بت
            await SetCustomerBalanceAsync(db, seed.CustomerId, 100m);
        }

        await using (var db = _fx.CreateContext())
        {
            var payments = new clsPaymentService(db);

            // مصرّح (بت العملاء) — بلا جلسة درج: تحصيل خلفي مشروع
            var ok = await payments.ReceiveCustomerPaymentAsync(
                PayRequest(null, seed.CustomerId, 30m, collectorId, null));
            Assert.True(ok.IsSuccess, ok.Message);

            // غير مصرّح (بت البيع فقط) → رفض
            var denied = await payments.ReceiveCustomerPaymentAsync(
                PayRequest(null, seed.CustomerId, 10m, clerkId, null));
            Assert.False(denied.IsSuccess);
            Assert.Contains("صلاحية", denied.Message);

            // محاولة تجاوز الصلاحية بـ SessionId = null → رفض أيضًا
            var bypass = await payments.ReceiveCustomerPaymentAsync(
                PayRequest(null, seed.CustomerId, 10m, debtorId, null));
            Assert.False(bypass.IsSuccess);
        }
    }

    // ══ 9) سداد المورد: بت شاشة الموردين (8) — بت فواتير الشراء وحده لا يكفي ══
    [Fact]
    public async Task PaySupplier_RequiresSuppliersBit()
    {
        string marker = $"au9-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int payerId, purchaserId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, marker);
            payerId = await SeedUserAsync(db, marker + "-pay", AppPermissions.Suppliers);          // بت 8 فقط
            purchaserId = await SeedUserAsync(db, marker + "-pur", AppPermissions.PurchaseInvoices); // بت 32 فقط
            await SetSupplierBalanceAsync(db, seed.SupplierId, 50m);
        }

        await using (var db = _fx.CreateContext())
        {
            var payments = new clsPaymentService(db);

            var ok = await payments.PaySupplierAsync(PayRequest(null, seed.SupplierId, 20m, payerId, null));
            Assert.True(ok.IsSuccess, ok.Message);

            var denied = await payments.PaySupplierAsync(PayRequest(null, seed.SupplierId, 10m, purchaserId, null));
            Assert.False(denied.IsSuccess);
            Assert.Contains("صلاحية", denied.Message);
        }
    }

    // ══ 10) سداد فاتورة: البت يتبع نوع الفاتورة (بيع 16 / شراء 32) ══
    [Fact]
    public async Task PayInvoice_PermissionFollowsInvoiceType()
    {
        string marker = $"au10-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int saleId, purchaseId, salesClerkId, purchaseClerkId, wrongBitId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, marker);
            salesClerkId = await SeedUserAsync(db, marker + "-sc", AppPermissions.SalesInvoices);    // بت 16 فقط
            purchaseClerkId = await SeedUserAsync(db, marker + "-pc", AppPermissions.PurchaseInvoices); // بت 32 فقط
            wrongBitId = await SeedUserAsync(db, marker + "-wb", AppPermissions.Customers);          // بت 4 فقط
            purchaseId = await PurchaseAsync(db, seed, seed.UserId, cashPaid: 30m); // 100 مدفوع 30
        }

        await using (var db = _fx.CreateContext())
            saleId = await CreditSaleAsync(db, seed, null, 5, 20m); // دين 100

        await using (var db = _fx.CreateContext())
        {
            var payments = new clsPaymentService(db);

            // فاتورة بيع: مصرّح بالبت 16 → نجاح
            var okSale = await payments.PayInvoiceAsync(PayRequest(saleId, seed.CustomerId, 20m, salesClerkId, null));
            Assert.True(okSale.IsSuccess, okSale.Message);

            // فاتورة بيع: صاحب بت خاطئ (العملاء فقط) → رفض
            var deniedSale = await payments.PayInvoiceAsync(PayRequest(saleId, seed.CustomerId, 10m, wrongBitId, null));
            Assert.False(deniedSale.IsSuccess);
            Assert.Contains("صلاحية", deniedSale.Message);

            // فاتورة شراء: مصرّح بالبت 32 → نجاح
            var okPurchase = await payments.PayInvoiceAsync(PayRequest(purchaseId, seed.SupplierId, 20m, purchaseClerkId, null));
            Assert.True(okPurchase.IsSuccess, okPurchase.Message);

            // فاتورة شراء: صاحب بت البيع فقط → رفض
            var deniedPurchase = await payments.PayInvoiceAsync(PayRequest(purchaseId, seed.SupplierId, 10m, salesClerkId, null));
            Assert.False(deniedPurchase.IsSuccess);
        }
    }

    // ══ 11) دفعة بجلسة مفتوحة صالحة تُقيد لدرجها؛ والتحصيل الخلفي بلا جلسة مشروع ══
    [Fact]
    public async Task Payment_OpenSessionBindsDrawer_NullSessionStillLegit()
    {
        string marker = $"au11-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int sessionId, collectorId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, marker);
            collectorId = await SeedUserAsync(db, marker + "-col", AppPermissions.Customers);
            await SetCustomerBalanceAsync(db, seed.CustomerId, 80m);
        }

        // التحصيل بجلسة صاحبها هو نفسه → يُقيد لدرجها
        await using (var db = _fx.CreateContext())
        {
            sessionId = await OpenSessionAsync(db, collectorId, 10m);
            var ok = await new clsPaymentService(db).ReceiveCustomerPaymentAsync(
                PayRequest(null, seed.CustomerId, 30m, collectorId, sessionId));
            Assert.True(ok.IsSuccess, ok.Message);

            var refund = await db.Payments.AsNoTracking()
                .SingleAsync(p => p.InvoiceId == null && p.PartyId == seed.CustomerId && p.Amount == 30m);
            Assert.Equal(sessionId, refund.SessionId);
        }

        // جلسة مستخدم آخر (غير مدير) → رفض ملكية
        await using (var db = _fx.CreateContext())
        {
            var other = await SeedUserAsync(db, marker + "-o", AppPermissions.Customers);
            await OpenSessionAsync(db, other, 5m);
            var denied = await new clsPaymentService(db).ReceiveCustomerPaymentAsync(
                PayRequest(null, seed.CustomerId, 5m, other, sessionId));
            Assert.False(denied.IsSuccess);
            Assert.Contains("مستخدم آخر", denied.Message);

            // وبلا جلسة: نفس المستخدم المصرّح له — مشروع (خلفية)
            var legacy = await new clsPaymentService(db).ReceiveCustomerPaymentAsync(
                PayRequest(null, seed.CustomerId, 5m, other, null));
            Assert.True(legacy.IsSuccess, legacy.Message);
        }
    }
}
