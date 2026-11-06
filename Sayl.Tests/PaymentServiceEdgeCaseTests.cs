using Common.DTOs.RequestModels;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// سلوكيات الدفع اللاحق (clsPaymentService) التي لم تكن مغطاة سابقًا في xunit:
/// المبالغ السالبة، الدفع بالمتبقي بالضبط، التجاوز، الازدواجية (بما فيها الحالة
/// ذات الحالة القديمة stale status)، التزامن على نفس الفاتورة، وفشل قاعدة
/// البيانات منتصف المعاملة (الذرّية: لا دفعة بلا تحديث رصيد ولا عكس).
/// </summary>
[Collection("database")]
public class PaymentServiceEdgeCaseTests
{
    private readonly DatabaseFixture _fx;
    public PaymentServiceEdgeCaseTests(DatabaseFixture fx) => _fx = fx;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    /// <summary>فاتورة بيع آجلة مكتملة (بلا أي سداد) بقيمة محددة.</summary>
    private static async Task<int> SeedCreditSaleAsync(SaylDbContext db, TestSeed seed, decimal net)
    {
        var invoice = new Invoice
        {
            Type = enInvoicesType.sale,
            Status = enInvoicesStatus.completed,
            PaymentStatus = enPaymentStatus.unpaid,
            PartyId = seed.CustomerId,
            PartyType = 'C',
            UserId = seed.UserId,
            TotalAmount = net,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            NetAmount = net,
            CreatedAt = DateTime.UtcNow
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice.InvoiceId;
    }

    /// <summary>فاتورة شراء آجلة مكتملة (بلا أي سداد) بقيمة محددة.</summary>
    private static async Task<int> SeedCreditPurchaseAsync(SaylDbContext db, TestSeed seed, decimal net)
    {
        var invoice = new Invoice
        {
            Type = enInvoicesType.purchase,
            Status = enInvoicesStatus.completed,
            PaymentStatus = enPaymentStatus.unpaid,
            PartyId = seed.SupplierId,
            PartyType = 'S',
            UserId = seed.UserId,
            TotalAmount = net,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            NetAmount = net,
            CreatedAt = DateTime.UtcNow
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice.InvoiceId;
    }

    private static DtoPaymentRequest Pay(int? invoiceId, int? partyId, decimal amount, int userId) =>
        new() { InvoiceId = invoiceId, PartyId = partyId, Amount = amount, PaymentDate = Today, Method = "cash", UserId = userId };

    private static async Task<decimal> CustomerBalanceAsync(SaylDbContext db, int customerId) =>
        await db.Customers.AsNoTracking().Where(c => c.PartyId == customerId).Select(c => c.Balance ?? 0m).SingleAsync();

    private static async Task<decimal> SupplierBalanceAsync(SaylDbContext db, int supplierId) =>
        await db.Suppliers.AsNoTracking().Where(s => s.PartyId == supplierId).Select(s => s.Balance ?? 0m).SingleAsync();

    // ══ 1) دفعة لاحقة صالحة: تُسجَّل، الحالة جزئية، الرصيد يُخفَض بمقدارها ══
    [Fact]
    public async Task ValidSubsequentPayment_RecordsPayment_PartialStatus_ReducesBalance()
    {
        string marker = $"vsp-{Guid.NewGuid():N}"[..12];
        int userId, customerId, invoiceId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; customerId = seed.CustomerId;
            await db.Customers.Where(c => c.PartyId == customerId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 100m));
            invoiceId = await SeedCreditSaleAsync(db, seed, 100m);
        }

        await using (var db = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db).PayInvoiceAsync(Pay(invoiceId, customerId, 40m, userId));
            Assert.True(r.IsSuccess, r.Message);
        }

        await using (var verify = _fx.CreateContext())
        {
            var invoice = await verify.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceId == invoiceId);
            Assert.Equal(enPaymentStatus.partial, invoice.PaymentStatus);
            Assert.Equal(60m, await CustomerBalanceAsync(verify, customerId));
            Assert.Equal(1, await verify.Payments.CountAsync(p => p.InvoiceId == invoiceId));
        }
    }

    // ══ 2) مبلغ سالب (وصفر): يُرفض على المسارات الثلاثة ولا يُسجَّل شيء ══
    [Fact]
    public async Task NegativeAndZeroPayments_AreRejected_OnAllThreePaths()
    {
        string marker = $"ngz-{Guid.NewGuid():N}"[..12];
        int userId, customerId, supplierId, invoiceId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; customerId = seed.CustomerId; supplierId = seed.SupplierId;
            await db.Customers.Where(c => c.PartyId == customerId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 50m));
            await db.Suppliers.Where(s => s.PartyId == supplierId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Balance, 50m));
            invoiceId = await SeedCreditSaleAsync(db, seed, 100m);
        }

        var svc = new clsPaymentService(_fx.CreateContext());

        foreach (var amount in new[] { -5m, 0m })
        {
            Assert.False((await svc.PayInvoiceAsync(Pay(invoiceId, customerId, amount, userId))).IsSuccess, $"invoice pay {amount}");
            Assert.False((await svc.ReceiveCustomerPaymentAsync(Pay(null, customerId, amount, userId))).IsSuccess, $"collect {amount}");
            Assert.False((await svc.PaySupplierAsync(Pay(null, supplierId, amount, userId))).IsSuccess, $"supplier pay {amount}");
        }

        await using (var verify = _fx.CreateContext())
        {
            Assert.Equal(0, await verify.Payments.CountAsync(p => p.PartyId == customerId || p.PartyId == supplierId));
            Assert.Equal(50m, await CustomerBalanceAsync(verify, customerId));
            Assert.Equal(50m, await SupplierBalanceAsync(verify, supplierId));
        }
    }

    // ══ 3) دفعة تساوي المتبقي بالضبط: الحالة مدفوعة والرصيد صفر ══
    [Fact]
    public async Task PaymentEqualToRemaining_SetsPaidStatus_AndZeroesDebt()
    {
        string marker = $"per-{Guid.NewGuid():N}"[..12];
        int userId, supplierId, invoiceId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; supplierId = seed.SupplierId;
            await db.Suppliers.Where(s => s.PartyId == supplierId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Balance, 120m));
            invoiceId = await SeedCreditPurchaseAsync(db, seed, 120m);
        }

        await using (var db = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db).PayInvoiceAsync(Pay(invoiceId, supplierId, 120m, userId));
            Assert.True(r.IsSuccess, r.Message);
        }

        await using (var verify = _fx.CreateContext())
        {
            var invoice = await verify.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceId == invoiceId);
            Assert.Equal(enPaymentStatus.paid, invoice.PaymentStatus);
            Assert.Equal(0m, await SupplierBalanceAsync(verify, supplierId));
        }
    }

    // ══ 4) دفعة تتجاوز المتبقي: تُرفض — لا دفعة ولا تغيير رصيد ولا حالة ══
    [Fact]
    public async Task PaymentExceedingRemaining_IsRejected_LeavesNoTrace()
    {
        string marker = $"pex-{Guid.NewGuid():N}"[..12];
        int userId, supplierId, invoiceId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; supplierId = seed.SupplierId;
            await db.Suppliers.Where(s => s.PartyId == supplierId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Balance, 100m));
            invoiceId = await SeedCreditPurchaseAsync(db, seed, 100m);
        }

        await using (var db = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db).PayInvoiceAsync(Pay(invoiceId, supplierId, 100.01m, userId));
            Assert.False(r.IsSuccess);
        }

        await using (var verify = _fx.CreateContext())
        {
            var invoice = await verify.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceId == invoiceId);
            Assert.Equal(enPaymentStatus.unpaid, invoice.PaymentStatus);
            Assert.Equal(100m, await SupplierBalanceAsync(verify, supplierId));
            Assert.Equal(0, await verify.Payments.CountAsync(p => p.InvoiceId == invoiceId));
        }
    }

    // ══ 5) ازدواجية السداد: فاتورة مسددة بالكامل — حتى بحالة قديمة stale يمنعها التخصيص ══
    [Fact]
    public async Task DuplicatePayment_OnFullyPaidInvoice_IsRejected_EvenWithStaleStatus()
    {
        string marker = $"dup-{Guid.NewGuid():N}"[..12];
        int userId, customerId, invoiceId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; customerId = seed.CustomerId;
            await db.Customers.Where(c => c.PartyId == customerId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 100m));
            invoiceId = await SeedCreditSaleAsync(db, seed, 100m);
        }

        // سداد كامل
        await using (var db = _fx.CreateContext())
        {
            var r = await new clsPaymentService(db).PayInvoiceAsync(Pay(invoiceId, customerId, 100m, userId));
            Assert.True(r.IsSuccess, r.Message);
        }

        // إعادة الإرسال بحالة سليمة
        await using (var db = _fx.CreateContext())
        {
            var dup = await new clsPaymentService(db).PayInvoiceAsync(Pay(invoiceId, customerId, 10m, userId));
            Assert.False(dup.IsSuccess, "duplicate payment on paid invoice must be rejected");
        }

        // حتى بحالة PaymentStatus قديمة (unpaid) — التخصيص الحتمي هو الحارس الفعلي
        await using (var db = _fx.CreateContext())
        {
            await db.Invoices.Where(i => i.InvoiceId == invoiceId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.PaymentStatus, enPaymentStatus.unpaid));
            var dupStale = await new clsPaymentService(db).PayInvoiceAsync(Pay(invoiceId, customerId, 10m, userId));
            Assert.False(dupStale.IsSuccess, "duplicate payment must be blocked by allocation despite stale status");
        }

        await using (var verify = _fx.CreateContext())
        {
            Assert.Equal(1, await verify.Payments.CountAsync(p => p.InvoiceId == invoiceId));
            Assert.Equal(0m, await CustomerBalanceAsync(verify, customerId));
        }
    }

    // ══ 6) سدادان متزامنان على نفس الفاتورة: واحد فقط ينجح — لا تجاوز للمتبقي ══
    [Fact]
    public async Task ConcurrentInvoicePayments_CannotCollectivelyExceedOutstanding()
    {
        string marker = $"cip-{Guid.NewGuid():N}"[..12];
        int userId, customerId, invoiceId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; customerId = seed.CustomerId;
            await db.Customers.Where(c => c.PartyId == customerId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 100m));
            invoiceId = await SeedCreditSaleAsync(db, seed, 100m);
        }

        await using var db1 = _fx.CreateContext();
        await using var db2 = _fx.CreateContext();
        var svc1 = new clsPaymentService(db1);
        var svc2 = new clsPaymentService(db2);

        var results = await Task.WhenAll(
            svc1.PayInvoiceAsync(Pay(invoiceId, customerId, 100m, userId)),
            svc2.PayInvoiceAsync(Pay(invoiceId, customerId, 100m, userId)));

        int successes = results.Count(r => r.IsSuccess);
        Assert.Equal(1, successes); // واحد فقط يمرّ — لا سداد مزدوج ولا رفض مزدوج

        await using (var verify = _fx.CreateContext())
        {
            var invoice = await verify.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceId == invoiceId);
            var linkedPaid = await verify.Payments.Where(p => p.InvoiceId == invoiceId).SumAsync(p => (decimal?)p.Amount) ?? 0m;
            Assert.Equal(100m, linkedPaid);                    // ما قُيّد = المتبقي بالضبط
            Assert.Equal(enPaymentStatus.paid, invoice.PaymentStatus);
            Assert.Equal(0m, await CustomerBalanceAsync(verify, customerId)); // الرصيد خُفِض مرة واحدة
        }
    }

    // ══ 7) فشل قاعدة البيانات بعد تحديث الرصيد: تراجع كامل — لا دفعة ولا أثر ══
    [Fact]
    public async Task DbFailureAfterBalanceUpdate_RollsBackEverything_PayInvoice()
    {
        string marker = $"dfp-{Guid.NewGuid():N}"[..12];
        int userId, customerId, invoiceId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; customerId = seed.CustomerId;
            await db.Customers.Where(c => c.PartyId == customerId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 100m));
            invoiceId = await SeedCreditSaleAsync(db, seed, 100m);
        }

        // سياق يُفشل SaveChangesAsync بعد أن نُفِّذ تحديث الرصيد الفعلي (ExecuteUpdate)
        var options = new DbContextOptionsBuilder<SaylDbContext>().UseSqlServer(DatabaseFixture.ConnectionString).Options;
        await using (var failing = new ExplodingSaveContext(options))
        {
            var r = await new clsPaymentService(failing).PayInvoiceAsync(Pay(invoiceId, customerId, 40m, userId));
            Assert.False(r.IsSuccess, "SaveChanges failure must surface as a failed payment");
        }

        await using (var verify = _fx.CreateContext())
        {
            var invoice = await verify.Invoices.AsNoTracking().SingleAsync(i => i.InvoiceId == invoiceId);
            Assert.Equal(enPaymentStatus.unpaid, invoice.PaymentStatus); // الحالة لم تُمسّ
            Assert.Equal(100m, await CustomerBalanceAsync(verify, customerId)); // الرصيد تراجع
            Assert.Equal(0, await verify.Payments.CountAsync(p => p.InvoiceId == invoiceId)); // لا دفعة يتيمة
        }
    }

    // ══ 8) فشل قاعدة البيانات أثناء التحصيل العام: تراجع كامل ══
    [Fact]
    public async Task DbFailureAfterBalanceUpdate_RollsBackEverything_GeneralCollection()
    {
        string marker = $"dfc-{Guid.NewGuid():N}"[..12];
        int userId, customerId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; customerId = seed.CustomerId;
            await db.Customers.Where(c => c.PartyId == customerId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Balance, 80m));
        }

        var options = new DbContextOptionsBuilder<SaylDbContext>().UseSqlServer(DatabaseFixture.ConnectionString).Options;
        await using (var failing = new ExplodingSaveContext(options))
        {
            var r = await new clsPaymentService(failing).ReceiveCustomerPaymentAsync(Pay(null, customerId, 30m, userId));
            Assert.False(r.IsSuccess);
        }

        await using (var verify = _fx.CreateContext())
        {
            Assert.Equal(80m, await CustomerBalanceAsync(verify, customerId));
            Assert.Equal(0, await verify.Payments.CountAsync(p => p.PartyId == customerId && p.PartyType == 'C'));
        }
    }

    /// <summary>سياق يحاكي انهيار قاعدة البيانات عند الحفظ — بعد تنفيذ ExecuteUpdate فعليًا.</summary>
    private sealed class ExplodingSaveContext : SaylDbContext
    {
        public ExplodingSaveContext(DbContextOptions<SaylDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated-db-failure");

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated-db-failure");
    }
}
