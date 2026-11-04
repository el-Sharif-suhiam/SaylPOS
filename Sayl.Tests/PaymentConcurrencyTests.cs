using Common.DTOs.RequestModels;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// اختبارات التزامنية: تتأكد من عدم فقدان تحديثات الرصيد عند تنفيذ
/// عمليات التحصيل والدفع المتزامنة على نفس العميل أو المورد.
///
/// المشكلة الأصلية: الكيان (عميل/مورد) كان يُحمَّل قبل بدء المعاملة،
/// والرصيد يُقرأ من الكيان المتتبع (stale) بدلًا من البيانات النظيفة.
///
/// الإصلاح: قراءة الرصيد داخل المعاملة بدون تتبع + تحديث مباشر (ExecuteUpdateAsync).
/// </summary>
[Collection("database")]
public class PaymentConcurrencyTests
{
    private readonly DatabaseFixture _fx;
    public PaymentConcurrencyTests(DatabaseFixture fx) => _fx = fx;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    private static TestSeed SeedOf(int userId, int supplierId, int customerId, int productId) =>
        new() { UserId = userId, SupplierId = supplierId, CustomerId = customerId, ProductId = productId };

    // ══ 1) تحصيلان متتاليان على نفس العميل ══
    [Fact]
    public async Task SequentialCustomerPayments_MaintainCorrectBalance()
    {
        string marker = $"scc-{Guid.NewGuid():N}"[..12];
        int userId, customerId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; customerId = seed.CustomerId;
            await db.Customers.Where(c => c.PartyId == customerId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 100m));
        }

        // تحصيل أول: 40
        await using (var db1 = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db1).ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            {
                PartyId = customerId, Amount = 40m, PaymentDate = Today,
                Method = "cash", UserId = userId
            });
            Assert.True(r.IsSuccess, r.Message);
        }

        // تحصيل ثاني: 30
        await using (var db2 = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db2).ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            {
                PartyId = customerId, Amount = 30m, PaymentDate = Today,
                Method = "cash", UserId = userId
            });
            Assert.True(r.IsSuccess, r.Message);
        }

        // الرصيد النهائي: 100 - 40 - 30 = 30
        await using (var verifyDb = _fx.CreateContext())
        {
            var balance = await verifyDb.Customers.AsNoTracking()
                .Where(c => c.PartyId == customerId)
                .Select(c => c.Balance ?? 0m)
                .SingleAsync();
            Assert.Equal(30m, balance);
        }

        // عدد الدفعات: 2
        await using (var verifyDb = _fx.CreateContext())
        {
            var count = await verifyDb.Payments
                .Where(p => p.PartyId == customerId && p.PartyType == 'C')
                .CountAsync();
            Assert.Equal(2, count);
        }
    }

    // ══ 2) سدادان متتاليان على نفس المورد ══
    [Fact]
    public async Task SequentialSupplierPayments_MaintainCorrectBalance()
    {
        string marker = $"ssp-{Guid.NewGuid():N}"[..12];
        int userId, supplierId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; supplierId = seed.SupplierId;
            await db.Suppliers.Where(s => s.PartyId == supplierId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Balance, 200m));
        }

        // سداد أول: 50
        await using (var db1 = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db1).PaySupplierAsync(new DtoPaymentRequest
            {
                PartyId = supplierId, Amount = 50m, PaymentDate = Today,
                Method = "cash", UserId = userId
            });
            Assert.True(r.IsSuccess, r.Message);
        }

        // سداد ثاني: 70
        await using (var db2 = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db2).PaySupplierAsync(new DtoPaymentRequest
            {
                PartyId = supplierId, Amount = 70m, PaymentDate = Today,
                Method = "cash", UserId = userId
            });
            Assert.True(r.IsSuccess, r.Message);
        }

        // الرصيد النهائي: 200 - 50 - 70 = 80
        await using (var verifyDb = _fx.CreateContext())
        {
            var balance = await verifyDb.Suppliers.AsNoTracking()
                .Where(s => s.PartyId == supplierId)
                .Select(s => s.Balance ?? 0m)
                .SingleAsync();
            Assert.Equal(80m, balance);
        }
    }

    // ══ 3) دفعة تفشل لأن المبلغ أكبر من الرصيد بعد دفعة سابقة ══
    [Fact]
    public async Task CustomerPayment_FailsWhenExceedsCurrentBalance()
    {
        string marker = $"fec-{Guid.NewGuid():N}"[..12];
        int userId, customerId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; customerId = seed.CustomerId;
            await db.Customers.Where(c => c.PartyId == customerId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 50m));
        }

        // تحصيل أول: 30 (ناجح)
        await using (var db1 = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db1).ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            {
                PartyId = customerId, Amount = 30m, PaymentDate = Today,
                Method = "cash", UserId = userId
            });
            Assert.True(r.IsSuccess, r.Message);
        }

        // تحصيل ثاني: 30 (الرصيد المتبقي = 20، يفشل)
        await using (var db2 = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db2).ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            {
                PartyId = customerId, Amount = 30m, PaymentDate = Today,
                Method = "cash", UserId = userId
            });
            Assert.False(r.IsSuccess, "Should fail: 30 exceeds remaining balance of 20");
            Assert.Contains("أكبر من رصيد العميل", r.Message);
        }

        // الرصيد لا يزال 20
        await using (var verifyDb = _fx.CreateContext())
        {
            var balance = await verifyDb.Customers.AsNoTracking()
                .Where(c => c.PartyId == customerId)
                .Select(c => c.Balance ?? 0m)
                .SingleAsync();
            Assert.Equal(20m, balance);
        }
    }

    // ══ 4) دفعة تفشل على المورد لأن المبلغ أكبر من الرصيد ══
    [Fact]
    public async Task SupplierPayment_FailsWhenExceedsCurrentBalance()
    {
        string marker = $"fes-{Guid.NewGuid():N}"[..12];
        int userId, supplierId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; supplierId = seed.SupplierId;
            await db.Suppliers.Where(s => s.PartyId == supplierId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Balance, 40m));
        }

        // سداد أول: 25 (ناجح)
        await using (var db1 = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db1).PaySupplierAsync(new DtoPaymentRequest
            {
                PartyId = supplierId, Amount = 25m, PaymentDate = Today,
                Method = "cash", UserId = userId
            });
            Assert.True(r.IsSuccess, r.Message);
        }

        // سداد ثاني: 20 (الرصيد المتبقي = 15، يفشل)
        await using (var db2 = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db2).PaySupplierAsync(new DtoPaymentRequest
            {
                PartyId = supplierId, Amount = 20m, PaymentDate = Today,
                Method = "cash", UserId = userId
            });
            Assert.False(r.IsSuccess, "Should fail: 20 exceeds remaining balance of 15");
            Assert.Contains("أكبر من رصيد المورد", r.Message);
        }

        // الرصيد لا يزال 15
        await using (var verifyDb = _fx.CreateContext())
        {
            var balance = await verifyDb.Suppliers.AsNoTracking()
                .Where(s => s.PartyId == supplierId)
                .Select(s => s.Balance ?? 0m)
                .SingleAsync();
            Assert.Equal(15m, balance);
        }
    }

    // ══ 5) السداد الكامل للرصيد ══
    [Fact]
    public async Task CustomerPayment_ExactBalance_Succeeds()
    {
        string marker = $"pec-{Guid.NewGuid():N}"[..12];
        int userId, customerId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; customerId = seed.CustomerId;
            await db.Customers.Where(c => c.PartyId == customerId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 50m));
        }

        // تحصيل يساوي الرصيد بالكامل: 50
        await using (var db1 = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db1).ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            {
                PartyId = customerId, Amount = 50m, PaymentDate = Today,
                Method = "cash", UserId = userId
            });
            Assert.True(r.IsSuccess, r.Message);
        }

        // الرصيد = 0
        await using (var verifyDb = _fx.CreateContext())
        {
            var balance = await verifyDb.Customers.AsNoTracking()
                .Where(c => c.PartyId == customerId)
                .Select(c => c.Balance ?? 0m)
                .SingleAsync();
            Assert.Equal(0m, balance);
        }
    }

    // ══ 6) سداد يفشل لا يترك سجل دفعة ══
    [Fact]
    public async Task FailedPayment_LeavesNoPaymentRecord()
    {
        string marker = $"fpn-{Guid.NewGuid():N}"[..12];
        int userId, customerId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; customerId = seed.CustomerId;
            await db.Customers.Where(c => c.PartyId == customerId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 10m));
        }

        // محاولة تحصيل أكبر من الرصيد
        await using (var db1 = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db1).ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            {
                PartyId = customerId, Amount = 20m, PaymentDate = Today,
                Method = "cash", UserId = userId
            });
            Assert.False(r.IsSuccess);
        }

        // لا سجلات دفعة
        await using (var verifyDb = _fx.CreateContext())
        {
            var count = await verifyDb.Payments
                .Where(p => p.PartyId == customerId && p.PartyType == 'C')
                .CountAsync();
            Assert.Equal(0, count);
        }
    }

    // ══ 7) سداد يفشل على المورد لا يترك سجل ══
    [Fact]
    public async Task FailedSupplierPayment_LeavesNoPaymentRecord()
    {
        string marker = $"fps-{Guid.NewGuid():N}"[..12];
        int userId, supplierId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; supplierId = seed.SupplierId;
            await db.Suppliers.Where(s => s.PartyId == supplierId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Balance, 10m));
        }

        // محاولة سداد أكبر من الرصيد
        await using (var db1 = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db1).PaySupplierAsync(new DtoPaymentRequest
            {
                PartyId = supplierId, Amount = 20m, PaymentDate = Today,
                Method = "cash", UserId = userId
            });
            Assert.False(r.IsSuccess);
        }

        // لا سجلات دفعة
        await using (var verifyDb = _fx.CreateContext())
        {
            var count = await verifyDb.Payments
                .Where(p => p.PartyId == supplierId && p.PartyType == 'S')
                .CountAsync();
            Assert.Equal(0, count);
        }
    }

    // ══ 8) PayInvoiceAsync يحافظ على صحة الرصيد ══
    [Fact]
    public async Task PayInvoice_MaintainsCorrectBalance()
    {
        string marker = $"pi-{Guid.NewGuid():N}"[..12];
        int userId, supplierId, productId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; supplierId = seed.SupplierId; productId = seed.ProductId;
            await db.Suppliers.Where(s => s.PartyId == supplierId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Balance, 100m));
        }

        // إنشاء فاتورة شراء
        int invoiceId;
        await using (var db = _fx.CreateContext())
        {
            var r = await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
            {
                SupplierId = supplierId, UserId = userId,
                Items = { new DtoPurchaseItem { ProductId = productId, BatchNumber = $"B-{Guid.NewGuid():N}"[..12], Quantity = 5, UnitCost = 20m } }
            });
            Assert.True(r.IsSuccess, r.Message);
            invoiceId = r.Data;
        }

        // سداد جزئي: 60 من 100
        await using (var db = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db).PayInvoiceAsync(new DtoPaymentRequest
            {
                InvoiceId = invoiceId, Amount = 60m, PaymentDate = Today,
                Method = "cash", UserId = userId
            });
            Assert.True(r.IsSuccess, r.Message);
        }

        // الرصيد: 100 (مبدئي) + 100 (فاتورة الشراء) - 60 = 140
        await using (var verifyDb = _fx.CreateContext())
        {
            var balance = await verifyDb.Suppliers.AsNoTracking()
                .Where(s => s.PartyId == supplierId)
                .Select(s => s.Balance ?? 0m)
                .SingleAsync();
            Assert.Equal(140m, balance);
        }

        // سداد كامل المتبقي: 40
        await using (var db = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db).PayInvoiceAsync(new DtoPaymentRequest
            {
                InvoiceId = invoiceId, Amount = 40m, PaymentDate = Today,
                Method = "cash", UserId = userId
            });
            Assert.True(r.IsSuccess, r.Message);
        }

        // الرصيد: 140 - 40 = 100
        await using (var verifyDb = _fx.CreateContext())
        {
            var balance = await verifyDb.Suppliers.AsNoTracking()
                .Where(s => s.PartyId == supplierId)
                .Select(s => s.Balance ?? 0m)
                .SingleAsync();
            Assert.Equal(100m, balance);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // اختبارات التزامن الحقيقية: عمليتان على اتصالين وسياقين مستقلين
    // تنطلقان معًا (Task.WhenAll) على نفس الطرف — ضد SQL Server فعلي.
    // UPDLOCK على قراءة الرصيد يُسلسِل العمليتين: الثانية تنتظر ثم تقرأ
    // الرصيد الملتزم الجديد — فلا تحديث مفقود ولا deadlock ولا دفعة مكررة.
    // ═══════════════════════════════════════════════════════════════

    // ══ 9) تحصيلان متزامنان صالحان: كلاهما ينجح ولا تحديث يضيع ══
    [Fact]
    public async Task ConcurrentCustomerCollections_BothValid_NoLostUpdates()
    {
        string marker = $"cxc-{Guid.NewGuid():N}"[..12];
        int userId, customerId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; customerId = seed.CustomerId;
            await db.Customers.Where(c => c.PartyId == customerId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 100m));
        }

        await using var db1 = _fx.CreateContext();
        await using var db2 = _fx.CreateContext();
        var svc1 = new clsPaymentService(db1);
        var svc2 = new clsPaymentService(db2);

        var results = await Task.WhenAll(
            svc1.ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            { PartyId = customerId, Amount = 40m, PaymentDate = Today, Method = "cash", UserId = userId }),
            svc2.ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            { PartyId = customerId, Amount = 40m, PaymentDate = Today, Method = "cash", UserId = userId }));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Message)); // كلاهما نجح — لا ضحية deadlock

        // الرصيد: 100 - 40 - 40 = 20 — لا تحديث ضاع
        await using (var verifyDb = _fx.CreateContext())
        {
            var balance = await verifyDb.Customers.AsNoTracking()
                .Where(c => c.PartyId == customerId)
                .Select(c => c.Balance ?? 0m)
                .SingleAsync();
            Assert.Equal(20m, balance);
        }

        // سجلا دفعتان بالضبط — لا تكرار ولا نقص
        await using (var verifyDb = _fx.CreateContext())
        {
            var count = await verifyDb.Payments
                .Where(p => p.PartyId == customerId && p.PartyType == 'C')
                .CountAsync();
            Assert.Equal(2, count);
        }
    }

    // ══ 10) تحصيلان متزامنان يتجاوزان الرصيد معًا: واحد ينجح والثاني يُرفض برصيد حديث ══
    [Fact]
    public async Task ConcurrentCustomerCollections_ExceedingBalance_FailsCleanly()
    {
        string marker = $"cxb-{Guid.NewGuid():N}"[..12];
        int userId, customerId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; customerId = seed.CustomerId;
            await db.Customers.Where(c => c.PartyId == customerId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 50m));
        }

        // الرصيد 50 وتحصيلان متزامنان بمبلغ 40 لكل منهما — 80 > 50
        await using var db1 = _fx.CreateContext();
        await using var db2 = _fx.CreateContext();
        var svc1 = new clsPaymentService(db1);
        var svc2 = new clsPaymentService(db2);

        var results = await Task.WhenAll(
            svc1.ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            { PartyId = customerId, Amount = 40m, PaymentDate = Today, Method = "cash", UserId = userId }),
            svc2.ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            { PartyId = customerId, Amount = 40m, PaymentDate = Today, Method = "cash", UserId = userId }));

        int successes = results.Count(r => r.IsSuccess);
        Assert.Equal(1, successes); // واحد فقط يمرّ — التحقق على الرصيد الحالي داخل المعاملة

        // الرصيد: 50 - 40 = 10 — الفاشل لم يخفض شيئًا ولم يترك سجلًا
        await using (var verifyDb = _fx.CreateContext())
        {
            var balance = await verifyDb.Customers.AsNoTracking()
                .Where(c => c.PartyId == customerId)
                .Select(c => c.Balance ?? 0m)
                .SingleAsync();
            Assert.Equal(10m, balance);

            var count = await verifyDb.Payments
                .Where(p => p.PartyId == customerId && p.PartyType == 'C')
                .CountAsync();
            Assert.Equal(1, count);
        }
    }

    // ══ 11) سدادان متزامنان صالحان لمورد واحد: كلاهما ينجح ولا تحديث يضيع ══
    [Fact]
    public async Task ConcurrentSupplierPayments_BothValid_NoLostUpdates()
    {
        string marker = $"cxs-{Guid.NewGuid():N}"[..12];
        int userId, supplierId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; supplierId = seed.SupplierId;
            await db.Suppliers.Where(s => s.PartyId == supplierId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Balance, 200m));
        }

        await using var db1 = _fx.CreateContext();
        await using var db2 = _fx.CreateContext();
        var svc1 = new clsPaymentService(db1);
        var svc2 = new clsPaymentService(db2);

        var results = await Task.WhenAll(
            svc1.PaySupplierAsync(new DtoPaymentRequest
            { PartyId = supplierId, Amount = 80m, PaymentDate = Today, Method = "cash", UserId = userId }),
            svc2.PaySupplierAsync(new DtoPaymentRequest
            { PartyId = supplierId, Amount = 80m, PaymentDate = Today, Method = "cash", UserId = userId }));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));

        // الرصيد: 200 - 80 - 80 = 40 — لا تحديث ضاع
        await using (var verifyDb = _fx.CreateContext())
        {
            var balance = await verifyDb.Suppliers.AsNoTracking()
                .Where(s => s.PartyId == supplierId)
                .Select(s => s.Balance ?? 0m)
                .SingleAsync();
            Assert.Equal(40m, balance);

            var count = await verifyDb.Payments
                .Where(p => p.PartyId == supplierId && p.PartyType == 'S')
                .CountAsync();
            Assert.Equal(2, count);
        }
    }

    // ══ 12) عمليتان متزامنتان على طرفين مختلفين: كلاهما ينجح (لا تفسد UPDLOCK الاستقلال) ══
    [Fact]
    public async Task ConcurrentOps_DifferentParties_BothSucceed()
    {
        string marker = $"cxd-{Guid.NewGuid():N}"[..12];
        int userId, customerId, supplierId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; customerId = seed.CustomerId; supplierId = seed.SupplierId;
            await db.Customers.Where(c => c.PartyId == customerId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 60m));
            await db.Suppliers.Where(s => s.PartyId == supplierId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Balance, 70m));
        }

        await using var db1 = _fx.CreateContext();
        await using var db2 = _fx.CreateContext();
        var svc1 = new clsPaymentService(db1);
        var svc2 = new clsPaymentService(db2);

        var results = await Task.WhenAll(
            svc1.ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            { PartyId = customerId, Amount = 20m, PaymentDate = Today, Method = "cash", UserId = userId }),
            svc2.PaySupplierAsync(new DtoPaymentRequest
            { PartyId = supplierId, Amount = 30m, PaymentDate = Today, Method = "cash", UserId = userId }));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));

        await using (var verifyDb = _fx.CreateContext())
        {
            var customerBalance = await verifyDb.Customers.AsNoTracking()
                .Where(c => c.PartyId == customerId)
                .Select(c => c.Balance ?? 0m)
                .SingleAsync();
            Assert.Equal(40m, customerBalance);

            var supplierBalance = await verifyDb.Suppliers.AsNoTracking()
                .Where(s => s.PartyId == supplierId)
                .Select(s => s.Balance ?? 0m)
                .SingleAsync();
            Assert.Equal(40m, supplierBalance);
        }
    }
}
