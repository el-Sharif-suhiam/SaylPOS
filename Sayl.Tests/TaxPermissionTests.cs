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
/// VULN-44: بوابة صلاحية الضريبة في الخروج (نمط بوابة الخصم نفسه):
/// أي انحراف عن النسبة المعدّة في الإعدادات يتطلب بت شاشة فواتير البيع —
/// النسبة الافتراضية تمر للجميع (تُحمَّل للصندوق تلقائيًا). الواجهة تقفل
/// الحقل للبلا صلاحية، والبوابة الحقيقية خدمية.
/// إعداد الضريبة يُضبط ويُصفَّر في finally — قاعدة الاختبار مشتركة.
/// </summary>
[Collection("database")]
public class TaxPermissionTests
{
    private readonly DatabaseFixture _fx;
    public TaxPermissionTests(DatabaseFixture fx) => _fx = fx;

    private const decimal TestTaxPercent = 15m;

    private static async Task SetTaxAsync(SaylDbContext db, decimal value)
    {
        var row = await db.Settings.FindAsync(clsSettingsService.KeyTaxPercent);
        if (row == null)
        {
            db.Settings.Add(new Setting
            {
                SettingKey = clsSettingsService.KeyTaxPercent,
                SettingValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            row.SettingValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            row.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    /// <summary>مستخدم بدور محدد — الدور مُعاد استخدامه (RoleId tinyint).</summary>
    private static async Task<int> SeedUserAsync(SaylDbContext db, string marker, int roleValue)
    {
        var role = await TestSeed.GetOrCreateRoleAsync(db, roleValue);
        var user = new User { Username = $"user-{marker}", PasswordHash = "x", Role = role, IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    /// <summary>شراء حقيقي ثم منتج ممزوق بالبذرة — مخزون صالح للبيع.</summary>
    private async Task SeedStockAsync(int userId, int supplierId, int productId)
    {
        await using var db = _fx.CreateContext();
        var purchase = new clsPurchaseService(db);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.True((await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = supplierId,
            UserId = userId,
            Items = { new DtoPurchaseItem { ProductId = productId, BatchNumber = "TX-A", Quantity = 10, UnitCost = 10m, ExpiryDate = today.AddDays(60) } }
        })).IsSuccess);
    }

    private static DtoCheckoutRequest CheckoutRequest(int userId, decimal taxPercent, int qty = 1)
    {
        decimal subtotal = 20m * qty;
        decimal net = subtotal + subtotal * taxPercent / 100m; // الدفع يغطي الصافي بالضريبة — لا بيع بالدين
        return new DtoCheckoutRequest
        {
            UserId = userId,
            TaxPercent = taxPercent,
            Payment = new DtoPaymentInfo { Amount = net, Method = "cash" },
            Items = { new DtoCheckoutItem { ProductId = 0, ProductName = "x", Quantity = qty } }
        };
    }

    // ══ 1) دور مصرَّح (بت 16) يعدّل الضريبة عن الافتراضي → مقبول ══
    [Fact]
    public async Task PermittedRole_CanEditTax_CheckoutAccepted()
    {
        string marker = $"tx1-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int userId = await SeedUserAsync(db, marker + "-p", AppPermissions.SalesInvoices); // بت 16 فقط
        await SeedStockAsync(seed.UserId, seed.SupplierId, seed.ProductId);

        try
        {
            await SetTaxAsync(db, TestTaxPercent); // الافتراضي 15

            await using var co = _fx.CreateContext();
            var request = CheckoutRequest(userId, 20m); // انحراف عن الافتراضي
            request.Items[0].ProductId = seed.ProductId;
            var r = await new clsCheckoutService(co).ProcessCheckoutAsync(request);
            Assert.True(r.IsSuccess, r.Message);
        }
        finally
        {
            await SetTaxAsync(db, 0m); // تصفير — القاعدة مشتركة
        }
    }

    // ══ 2) دور بلا بت 16 يعدّل الضريبة → مرفوض، لا فاتورة، تدقيق رفض ══
    [Fact]
    public async Task NonPermittedRole_TaxDeviation_Rejected_NoInvoice_Audited()
    {
        string marker = $"tx2-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int userId = await SeedUserAsync(db, marker + "-c", 0); // كاشير — بلا بت
        await SeedStockAsync(seed.UserId, seed.SupplierId, seed.ProductId);

        try
        {
            await SetTaxAsync(db, TestTaxPercent);

            await using var co = _fx.CreateContext();
            var request = CheckoutRequest(userId, 0m); // تصفير الضريبة = انحراف
            request.Items[0].ProductId = seed.ProductId;
            var r = await new clsCheckoutService(co).ProcessCheckoutAsync(request);
            Assert.False(r.IsSuccess);
            Assert.Contains("الضريبة", r.Message);

            // لا فاتورة — القاعدة لم تُلمس
            int salesForProduct = await db.Invoices.CountAsync(i =>
                i.Type == enInvoicesType.sale && i.Status == enInvoicesStatus.completed &&
                i.InvoiceItems!.Any(ii => ii.ProductId == seed.ProductId));
            Assert.Equal(0, salesForProduct);

            // سجل تدقيق الرفض مكتوب
            Assert.True(await db.SystemLogs.AnyAsync(l =>
                l.Action == "CheckoutDenied" && l.UserId == userId && l.Details!.Contains("ضريبة")));
        }
        finally
        {
            await SetTaxAsync(db, 0m);
        }
    }

    // ══ 3) النسبة الافتراضية تمر للجميع — كاشير بلا بت يبيع بها ══
    [Fact]
    public async Task NonPermittedRole_DefaultTax_CheckoutAccepted()
    {
        string marker = $"tx3-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        int userId = await SeedUserAsync(db, marker + "-c", 0); // كاشير
        await SeedStockAsync(seed.UserId, seed.SupplierId, seed.ProductId);

        try
        {
            await SetTaxAsync(db, TestTaxPercent); // الافتراضي 15 — يُحمَّل للصندوق تلقائيًا

            await using var co = _fx.CreateContext();
            var request = CheckoutRequest(userId, TestTaxPercent); // نفس الافتراضي — لم يمسّه
            request.Items[0].ProductId = seed.ProductId;
            var r = await new clsCheckoutService(co).ProcessCheckoutAsync(request);
            Assert.True(r.IsSuccess, r.Message);

            // الضريبة حُسبت بالافتراضي فعليًا (صافي 23 = 20 + 3 ضريبة)
            var invoice = await db.Invoices.AsNoTracking()
                .FirstAsync(i => i.InvoiceId == r.Data);
            decimal expectedTax = Math.Round(20m * TestTaxPercent / 100m, 2);
            Assert.Equal(expectedTax, invoice.TaxAmount);
        }
        finally
        {
            await SetTaxAsync(db, 0m);
        }
    }
}
