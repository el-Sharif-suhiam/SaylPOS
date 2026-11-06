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
/// VULN-05: إسناد الجلسات النقدية — تحصيل دَين قديم اليوم يخص درج اليوم،
/// لا يجوز الدفع على جلسة مغلقة، ولا يغلق الكاشير درج زميله.
/// (سياق جديد لكل خطوة — كما في ReportCalculationTests.)
/// </summary>
[Collection("database")]
public class SessionAttributionTests
{
    private readonly DatabaseFixture _fx;
    public SessionAttributionTests(DatabaseFixture fx) => _fx = fx;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    private static async Task<int> SeedUserAsync(SaylDbContext db, string marker, int roleValue)
    {
        // الدور مُعاد استخدامه من TestSeed.GetOrCreateRoleAsync (RoleId tinyint — لا دور جديد لكل اختبار)
        var role = await TestSeed.GetOrCreateRoleAsync(db, roleValue);
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

    private static async Task<int> CreditSaleAsync(SaylDbContext db, TestSeed seed, int? sessionId, int qty, decimal unitPrice)
    {
        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            CustomerId = seed.CustomerId,
            UserId = seed.UserId,
            SessionId = sessionId,
            Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = qty, UnitPrice = unitPrice } },
            Payment = new DtoPaymentInfo { Amount = 0m, Method = "cash", PaymentDate = Today }
        });
        Assert.True(r.IsSuccess, r.Message);
        return r.Data;
    }

    // ══ 1) تحصيل دَين قديم في جلسة جديدة ══
    [Fact]
    public async Task OldDebtCollection_BelongsToActingSession()
    {
        string marker = $"at1-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int session1;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, marker);
            session1 = await OpenSessionAsync(db, seed.UserId, 100m);
        }

        // مخزون للبيع
        await using (var db = _fx.CreateContext())
        {
            var r = await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
            {
                SupplierId = seed.SupplierId,
                UserId = seed.UserId,
                Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = $"B-{marker}", Quantity = 10, UnitCost = 10m } }
            });
            Assert.True(r.IsSuccess, r.Message);
        }

        // بيع آجل (دَين) في الجلسة القديمة
        int debtInvoiceId;
        await using (var db = _fx.CreateContext())
            debtInvoiceId = await CreditSaleAsync(db, seed, session1, 5, 20m);
        _ = debtInvoiceId;

        // إغلاق الجلسة القديمة (المالك يغلق جلسته)
        await using (var db = _fx.CreateContext())
        {
            var closed = await new clsPosSessionService(db).CloseSessionAsync(session1, 100m, actorUserId: seed.UserId);
            Assert.True(closed.IsSuccess, closed.Message);
        }

        // اليوم: جلسة جديدة وتحصيل الدَّين القديم نقدًا
        int session2;
        await using (var db = _fx.CreateContext())
            session2 = await OpenSessionAsync(db, seed.UserId, 50m);

        await using (var db = _fx.CreateContext())
        {
            var pay = await new clsPaymentService(db).ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            {
                PartyId = seed.CustomerId,
                Amount = 40m,
                PaymentDate = Today,
                Method = "cash",
                SessionId = session2,
                UserId = seed.UserId
            });
            Assert.True(pay.IsSuccess, pay.Message);
        }

        // الدفعة تُنسب لجلسة الفعل (اليوم) لا لجلسة الفاتورة
        await using (var db = _fx.CreateContext())
        {
            var paymentRow = await db.Payments.AsNoTracking()
                .SingleAsync(p => p.InvoiceId == null && p.PartyId == seed.CustomerId && p.Amount == 40m);
            Assert.Equal(session2, paymentRow.SessionId);

            // ملخص الجلسة الجديدة: 50 افتتاحي + 40 تحصيل = 90
            var newSummary = await new clsCashService(db).GetSessionCashSummaryAsync(session2);
            Assert.True(newSummary.IsSuccess, newSummary.Message);
            Assert.Equal(50m, newSummary.Data!.OpeningAmount);
            Assert.Equal(40m, newSummary.Data!.CashSales);
            Assert.Equal(90m, newSummary.Data!.ExpectedCash);

            // ملخص الجلسة القديمة لم يتأثر بالتحصيل المتأخر
            var oldSummary = await new clsCashService(db).GetSessionCashSummaryAsync(session1);
            Assert.Equal(0m, oldSummary.Data!.CashSales);
            Assert.Equal(100m, oldSummary.Data!.ExpectedCash);
        }
    }

    // ══ 2) الدفع على جلسة مغلقة / غير موجودة يُرفض ══
    [Fact]
    public async Task PaymentOnClosedOrMissingSession_IsRejected()
    {
        string marker = $"at2-{Guid.NewGuid():N}"[..12];
        TestSeed seed;
        int session1, debtInvoiceId;

        await using (var db = _fx.CreateContext())
        {
            seed = await TestSeed.SeedAsync(db, marker);
            session1 = await OpenSessionAsync(db, seed.UserId, 0m);
        }

        await using (var db = _fx.CreateContext())
        {
            var r = await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
            {
                SupplierId = seed.SupplierId,
                UserId = seed.UserId,
                Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = $"B-{marker}", Quantity = 10, UnitCost = 10m } }
            });
            Assert.True(r.IsSuccess, r.Message);
        }

        await using (var db = _fx.CreateContext())
            debtInvoiceId = await CreditSaleAsync(db, seed, session1, 2, 25m);

        await using (var db = _fx.CreateContext())
        {
            var closed = await new clsPosSessionService(db).CloseSessionAsync(session1, 0m, actorUserId: seed.UserId);
            Assert.True(closed.IsSuccess, closed.Message);
        }

        await using (var db = _fx.CreateContext())
        {
            var payments = new clsPaymentService(db);

            // دفع على جلسة مغلقة → رفض
            var onClosed = await payments.ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            {
                PartyId = seed.CustomerId, Amount = 10m, PaymentDate = Today, Method = "cash",
                SessionId = session1, UserId = seed.UserId
            });
            Assert.False(onClosed.IsSuccess);

            // سداد فاتورة قديمة بجلسة مغلقة → رفض
            var onClosedInvoice = await payments.PayInvoiceAsync(new DtoPaymentRequest
            {
                InvoiceId = debtInvoiceId, PartyId = seed.CustomerId, Amount = 10m,
                PaymentDate = Today, Method = "cash", SessionId = session1, UserId = seed.UserId
            });
            Assert.False(onClosedInvoice.IsSuccess);

            // دفع على جلسة غير موجودة → رفض
            var onMissing = await payments.ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            {
                PartyId = seed.CustomerId, Amount = 10m, PaymentDate = Today, Method = "cash",
                SessionId = 999999, UserId = seed.UserId
            });
            Assert.False(onMissing.IsSuccess);
        }
    }

    // ══ 3) الكاشير لا يغلق جلسة مستخدم آخر ══
    [Fact]
    public async Task CashierCannotCloseAnotherCashierSession()
    {
        string marker = $"at3-{Guid.NewGuid():N}"[..12];
        int adminId, cashierA, cashierB, sessionA;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker); // مدير
            adminId = seed.UserId;
            cashierA = await SeedUserAsync(db, marker + "-a", AppPermissions.CashierRoleValue);
            cashierB = await SeedUserAsync(db, marker + "-b", AppPermissions.CashierRoleValue);
            sessionA = await OpenSessionAsync(db, cashierA, 20m);
        }

        // كاشير آخر → رفض
        await using (var db = _fx.CreateContext())
        {
            var denied = await new clsPosSessionService(db).CloseSessionAsync(sessionA, 20m, actorUserId: cashierB);
            Assert.False(denied.IsSuccess);

            // الجلسة ما زالت مفتوحة
            var stillOpen = await db.PosSessions.AsNoTracking().SingleAsync(s => s.SessionId == sessionA);
            Assert.Equal(enPosSessionStatusType.open, stillOpen.Status);
        }

        // المالك يغلق جلسته → نجاح
        await using (var db = _fx.CreateContext())
        {
            var owner = await new clsPosSessionService(db).CloseSessionAsync(sessionA, 20m, actorUserId: cashierA);
            Assert.True(owner.IsSuccess, owner.Message);
        }

        // مدير يغلق جلسة كاشير آخر (جلسة جديدة للكاشير A) → نجاح
        int sessionA2;
        await using (var db = _fx.CreateContext())
            sessionA2 = await OpenSessionAsync(db, cashierA, 20m);

        await using (var db = _fx.CreateContext())
        {
            var byAdmin = await new clsPosSessionService(db).CloseSessionAsync(sessionA2, 20m, actorUserId: adminId);
            Assert.True(byAdmin.IsSuccess, byAdmin.Message);
        }
    }
}
