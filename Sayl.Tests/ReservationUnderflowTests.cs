using Common.DTOs.RequestModels;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// VULN-20: قصّصات «الأمان» في تحرير الحجوزات لم تكن صامتة بعد — تحت الطرح
/// الفعلي (قبل الطرح &lt; المطلق) يُسجَّل حدث تشخيصي واحد (ReservationUnderflow)
/// بمعرّف المنتج والفاتورة والقيمة قبل الطرح والمُطلق تحريرها، ويبقى المخزون
/// المحجوز 0 لا سالبًا. الطرح السليم لا يسجل شيئًا. يغطي الموقعين:
/// الإكمال (DeductStockAndCreateMovementsAsync) والإلغاء (ReleaseInvoiceReservationsAsync).
/// </summary>
[Collection("database")]
public class ReservationUnderflowTests
{
    private readonly DatabaseFixture _fx;
    public ReservationUnderflowTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>بذرة + مخزون 10 وحدات، ثم تعليق فاتورة 5 وحدات (حجز 5).</summary>
    private async Task<(int SuspendedInvoiceId, int ProductId, int UserId, int CustomerId)>
        SeedSuspendedWithReservationAsync(string marker)
    {
        int suspendedId, productId, userId, customerId;

        await using (var db = _fx.CreateContext())
        {
            var seed = await TestSeed.SeedAsync(db, marker);
            userId = seed.UserId; productId = seed.ProductId; customerId = seed.CustomerId;

            var r = await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
            {
                SupplierId = seed.SupplierId,
                UserId = userId,
                Items = { new DtoPurchaseItem { ProductId = productId, BatchNumber = $"B-{marker}", Quantity = 10, UnitCost = 10m } }
            });
            Assert.True(r.IsSuccess, r.Message);
        }

        await using (var db = _fx.CreateContext())
        {
            var r = await new clsCheckoutService(db).ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                CustomerId = customerId,
                UserId = userId,
                Items = { new DtoCheckoutItem { ProductId = productId, ProductName = "x", Quantity = 5, UnitPrice = 20m } },
                Payment = new DtoPaymentInfo { Amount = 0m, Method = "cash" }
            }, suspend: true);
            Assert.True(r.IsSuccess, r.Message);
            suspendedId = r.Data;
        }

        return (suspendedId, productId, userId, customerId);
    }

    /// <summary>محاكاة انحراف خارجي: خفض المحجوز المجمّع تحت قيمة الحجز المعلق.</summary>
    private async Task CorruptReservedAsync(int productId, int value)
    {
        await using var db = _fx.CreateContext();
        await db.Inventories.Where(i => i.ProductId == productId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.QuantityReserved, value));
    }

    // ══ 1) تحرير سليم: لا حدث تشخيصي إطلاقًا، والمحجوز يعود صفرًا ══
    [Fact]
    public async Task NormalRelease_NoUnderflowEvent()
    {
        string marker = $"ru1-{Guid.NewGuid():N}"[..12];
        var (suspendedId, productId, userId, _) = await SeedSuspendedWithReservationAsync(marker);

        await using var db = _fx.CreateContext();
        var r = await new clsCheckoutService(db).CompleteSuspendedInvoiceAsync(
            suspendedId,
            new List<DtoPaymentInfo> { new() { Amount = 100m, Method = "cash" } },
            userId);
        Assert.True(r.IsSuccess, r.Message);

        var inv = await db.Inventories.AsNoTracking().FirstAsync(i => i.ProductId == productId);
        Assert.Equal(0, inv.QuantityReserved ?? 0);   // الحجز حُرِّر كاملًا
        Assert.Equal(5, inv.QuantityAvailable ?? 0);  // و10 − 5 بِيعت

        Assert.False(await db.SystemLogs.AnyAsync(l =>
            l.Action == "ReservationUnderflow" && l.Details!.Contains($"الفاتورة #{suspendedId}")));
    }

    // ══ 2) تحت طرح فعلي عند الإكمال: حدث واحد بالتفاصيل + المحجوز 0 لا سالب ══
    [Fact]
    public async Task UnderflowOnComplete_OneDiagnosticEvent_ReservedNotNegative()
    {
        string marker = $"ru2-{Guid.NewGuid():N}"[..12];
        var (suspendedId, productId, userId, _) = await SeedSuspendedWithReservationAsync(marker);

        // انحراف خارجي: المحجوز 2 بينما الحجز المعلق 5
        await CorruptReservedAsync(productId, 2);

        await using var db = _fx.CreateContext();
        var r = await new clsCheckoutService(db).CompleteSuspendedInvoiceAsync(
            suspendedId,
            new List<DtoPaymentInfo> { new() { Amount = 100m, Method = "cash" } },
            userId);
        Assert.True(r.IsSuccess, r.Message);

        // حدث تشخيصي واحد بالتفاصيل المطلوبة
        var events = await db.SystemLogs.AsNoTracking()
            .Where(l => l.Action == "ReservationUnderflow" && l.Details!.Contains($"الفاتورة #{suspendedId}"))
            .ToListAsync();
        Assert.Single(events);
        Assert.Contains($"المنتج #{productId}", events[0].Details);
        Assert.Contains("قبل الطرح 2", events[0].Details);
        Assert.Contains("تحريرها 5", events[0].Details);

        // الحماية القائمة: المحجوز 0 لا سالب
        var inv = await db.Inventories.AsNoTracking().FirstAsync(i => i.ProductId == productId);
        Assert.Equal(0, inv.QuantityReserved ?? 0);
        Assert.True((inv.QuantityReserved ?? 0) >= 0);
    }

    // ══ 3) تحت طرح فعلي عند الإلغاء (الموقع الثاني): حدث واحد + المحجوز 0 ══
    [Fact]
    public async Task UnderflowOnCancel_OneDiagnosticEvent_ReservedNotNegative()
    {
        string marker = $"ru3-{Guid.NewGuid():N}"[..12];
        var (suspendedId, productId, userId, _) = await SeedSuspendedWithReservationAsync(marker);

        await CorruptReservedAsync(productId, 1); // المحجوز 1 بينما الحجز 5

        await using var db = _fx.CreateContext();
        var r = await new clsCheckoutService(db).CancelSuspendedInvoiceAsync(suspendedId, userId);
        Assert.True(r.IsSuccess, r.Message);

        var events = await db.SystemLogs.AsNoTracking()
            .Where(l => l.Action == "ReservationUnderflow" && l.Details!.Contains($"الفاتورة #{suspendedId}"))
            .ToListAsync();
        Assert.Single(events);
        Assert.Contains("قبل الطرح 1", events[0].Details);
        Assert.Contains("تحريرها 5", events[0].Details);

        var inv = await db.Inventories.AsNoTracking().FirstAsync(i => i.ProductId == productId);
        Assert.Equal(0, inv.QuantityReserved ?? 0);
    }
}
