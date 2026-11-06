using Common.DTOs;
using Common.DTOs.RequestModels;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Xunit;

namespace Sayl.Tests;

/// <summary>
/// Integration tests: combined parties (C/S/B), product deactivation with
/// stock, and unit-price override authorization at checkout.
/// </summary>
[Collection("database")]
public class PartyProductPriceTests
{
    private readonly DatabaseFixture _fx;
    public PartyProductPriceTests(DatabaseFixture fx) => _fx = fx;

    // ─────────────────────────────────────────────────
    // 1. Combined-party conversion (C → B and S → B)
    // ─────────────────────────────────────────────────

    [Fact]
    public async Task ConvertCustomerToCombined_CreatesMissingSupplierRecord()
    {
        string marker = $"ctc-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var partySvc = new clsPartyService(db);

        // Give the customer a balance + an invoice so history must survive
        var customer = await db.Customers.FindAsync(seed.CustomerId);
        customer!.Balance = 75m;
        await db.SaveChangesAsync();

        var result = await partySvc.UpdatePartyAsync(new DtoParty
        {
            PartyId = seed.CustomerId,
            PartyType = 'B',
            Name = "combined-from-customer"
        }, actorUserId: seed.UserId);

        Assert.True(result.IsSuccess, result.Message);

        // Missing supplier record created on the SAME PartyId, zero balance
        var supplier = await db.Suppliers.FindAsync(seed.CustomerId);
        Assert.NotNull(supplier);
        Assert.Equal(0m, supplier!.Balance ?? 0m);

        // Customer record (and its balance/history) untouched
        var custAfter = await db.Customers.FindAsync(seed.CustomerId);
        Assert.NotNull(custAfter);
        Assert.Equal(75m, custAfter!.Balance ?? 0m);

        var party = await db.Parties.FindAsync(seed.CustomerId);
        Assert.Equal('B', party!.PartyType);
    }

    [Fact]
    public async Task ConvertSupplierToCombined_CreatesMissingCustomerRecord()
    {
        string marker = $"cts-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var partySvc = new clsPartyService(db);

        var supplier = await db.Suppliers.FindAsync(seed.SupplierId);
        supplier!.Balance = 40m;
        await db.SaveChangesAsync();

        var result = await partySvc.UpdatePartyAsync(new DtoParty
        {
            PartyId = seed.SupplierId,
            PartyType = 'B',
            Name = "combined-from-supplier"
        }, actorUserId: seed.UserId);

        Assert.True(result.IsSuccess, result.Message);

        var customer = await db.Customers.FindAsync(seed.SupplierId);
        Assert.NotNull(customer);
        Assert.Equal(0m, customer!.Balance ?? 0m);
        Assert.Equal(0m, customer.CreditLimit ?? 0m);

        var supAfter = await db.Suppliers.FindAsync(seed.SupplierId);
        Assert.NotNull(supAfter);
        Assert.Equal(40m, supAfter!.Balance ?? 0m);

        var party = await db.Parties.FindAsync(seed.SupplierId);
        Assert.Equal('B', party!.PartyType);
    }

    [Fact]
    public async Task ConvertCombinedBackToSingleRole_IsRejected()
    {
        string marker = $"cbd-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var partySvc = new clsPartyService(db);

        Assert.True((await partySvc.UpdatePartyAsync(new DtoParty
        {
            PartyId = seed.CustomerId,
            PartyType = 'B'
        }, actorUserId: seed.UserId)).IsSuccess);

        // Downgrade B → C must be refused: it would hide one of the records
        var result = await partySvc.UpdatePartyAsync(new DtoParty
        {
            PartyId = seed.CustomerId,
            PartyType = 'C'
        }, actorUserId: seed.UserId);

        Assert.False(result.IsSuccess);
        // And both records still exist
        Assert.NotNull(await db.Customers.FindAsync(seed.CustomerId));
        Assert.NotNull(await db.Suppliers.FindAsync(seed.CustomerId));
    }

    // ─────────────────────────────────────────────────
    // 2. Combined-party deactivation with outstanding balances
    // ─────────────────────────────────────────────────

    [Fact]
    public async Task DeactivateCombinedParty_BlockedWhenCustomerBalanceOutstanding()
    {
        string marker = $"dcb-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var partySvc = new clsPartyService(db);

        Assert.True((await partySvc.UpdatePartyAsync(new DtoParty
        {
            PartyId = seed.CustomerId,
            PartyType = 'B'
        }, actorUserId: seed.UserId)).IsSuccess);

        var customer = await db.Customers.FindAsync(seed.CustomerId);
        customer!.Balance = 30m;
        await db.SaveChangesAsync();

        var result = await partySvc.DeactivatePartyAsync(seed.CustomerId, actorUserId: seed.UserId);
        Assert.False(result.IsSuccess);

        var party = await db.Parties.FindAsync(seed.CustomerId);
        Assert.True(party!.IsActive);
    }

    [Fact]
    public async Task DeactivateCombinedParty_BlockedWhenSupplierBalanceOutstanding()
    {
        string marker = $"dcs-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var partySvc = new clsPartyService(db);

        // Convert a supplier into a combined party, then give the SUPPLIER side a balance
        Assert.True((await partySvc.UpdatePartyAsync(new DtoParty
        {
            PartyId = seed.SupplierId,
            PartyType = 'B'
        }, actorUserId: seed.UserId)).IsSuccess);

        var supplier = await db.Suppliers.FindAsync(seed.SupplierId);
        supplier!.Balance = 55m;
        await db.SaveChangesAsync();

        var result = await partySvc.DeactivatePartyAsync(seed.SupplierId, actorUserId: seed.UserId);
        Assert.False(result.IsSuccess);

        var party = await db.Parties.FindAsync(seed.SupplierId);
        Assert.True(party!.IsActive);
    }

    [Fact]
    public async Task DeactivateCombinedParty_SucceedsWhenBothBalancesSettled()
    {
        string marker = $"dco-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var partySvc = new clsPartyService(db);

        Assert.True((await partySvc.UpdatePartyAsync(new DtoParty
        {
            PartyId = seed.CustomerId,
            PartyType = 'B'
        }, actorUserId: seed.UserId)).IsSuccess);

        var result = await partySvc.DeactivatePartyAsync(seed.CustomerId, actorUserId: seed.UserId);
        Assert.True(result.IsSuccess, result.Message);

        var party = await db.Parties.FindAsync(seed.CustomerId);
        Assert.False(party!.IsActive);
    }

    // ─────────────────────────────────────────────────
    // 3. Product deactivation with physical stock
    // ─────────────────────────────────────────────────

    [Fact]
    public async Task DeactivateProduct_WithBatchStock_IsBlocked()
    {
        string marker = $"dps-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var products = new clsProductService(db);

        // Physical stock: a purchase creates a batch with quantity
        var purchase = new clsPurchaseService(db);
        Assert.True((await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = $"B-{marker}", Quantity = 8, UnitCost = 5m } }
        })).IsSuccess);

        var result = await products.DeactivateProductAsync(seed.ProductId, seed.UserId);
        Assert.False(result.IsSuccess);

        var product = await db.Products.FindAsync(seed.ProductId);
        Assert.True(product!.IsActive);
    }

    [Fact]
    public async Task DeactivateProduct_WithZeroStock_Succeeds()
    {
        string marker = $"dpz-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var products = new clsProductService(db);

        // No stock at all — deactivation allowed
        var result = await products.DeactivateProductAsync(seed.ProductId, seed.UserId);
        Assert.True(result.IsSuccess, result.Message);

        var product = await db.Products.FindAsync(seed.ProductId);
        Assert.False(product!.IsActive);
    }

    [Fact]
    public async Task DeactivateProduct_StockConsumedFirst_ThenDeactivationSucceeds()
    {
        string marker = $"dpc-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var products = new clsProductService(db);

        // Stock in, then fully sold out through the normal sales workflow
        var purchase = new clsPurchaseService(db);
        Assert.True((await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = $"B-{marker}", Quantity = 4, UnitCost = 5m } }
        })).IsSuccess);

        var checkout = new clsCheckoutService(_fx.CreateContext());
        Assert.True((await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            UserId = seed.UserId,
            Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 4 } },
            Payment = new DtoPaymentInfo { Amount = 80m, Method = "cash" }
        })).IsSuccess);

        var result = await products.DeactivateProductAsync(seed.ProductId, seed.UserId);
        Assert.True(result.IsSuccess, result.Message);
    }

    // ─────────────────────────────────────────────────
    // 4. Unit-price override authorization at checkout
    // ─────────────────────────────────────────────────

    private async Task<(int CashierId, int ManagerId)> SeedRolesAsync(SaylDbContext db, string marker)
    {
        // الأدوار مُعاد استخدامه من TestSeed.GetOrCreateRoleAsync (RoleId tinyint — لا دور جديد لكل اختبار)
        var cashierRole = await TestSeed.GetOrCreateRoleAsync(db, AppPermissions.CashierRoleValue);
        var managerRole = await TestSeed.GetOrCreateRoleAsync(db, AppPermissions.AdminRoleValue);

        var cashier = new User { Username = $"cashier-{marker}", PasswordHash = "x", Role = cashierRole, IsActive = true };
        var manager = new User { Username = $"manager-{marker}", PasswordHash = "x", Role = managerRole, IsActive = true };
        db.Users.AddRange(cashier, manager);
        await db.SaveChangesAsync();
        return (cashier.UserId, manager.UserId);
    }

    /// <summary>مخزون قابل للبيع: فاتورة شراء تُنشئ الدفعة وصف المخزون.</summary>
    private async Task SeedStockAsync(SaylDbContext db, TestSeed seed, string marker, int qty)
    {
        var purchase = new clsPurchaseService(db);
        var result = await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = $"B-{marker}", Quantity = qty, UnitCost = 5m } }
        });
        Assert.True(result.IsSuccess, result.Message);
    }

    [Fact]
    public async Task Checkout_CashierSubmittingUnderpricedItem_IsRejected()
    {
        string marker = $"pu1-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var (cashierId, _) = await SeedRolesAsync(db, marker);
        await SeedStockAsync(db, seed, marker, 10);
        var checkout = new clsCheckoutService(_fx.CreateContext());

        // Base price 20 → cashier submits 5 (75% implicit discount)
        var result = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            UserId = cashierId,
            Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 1, UnitPrice = 5m } },
            Payment = new DtoPaymentInfo { Amount = 5m, Method = "cash" }
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("فواتير البيع", result.Message);

        // Nothing was written
        Assert.False(await db.Invoices.AnyAsync(i => i.UserId == cashierId));
    }

    [Fact]
    public async Task Checkout_CashierSubmittingNegativePrice_IsRejected()
    {
        string marker = $"pu2-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var (cashierId, _) = await SeedRolesAsync(db, marker);
        await SeedStockAsync(db, seed, marker, 10);
        var checkout = new clsCheckoutService(_fx.CreateContext());

        var result = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            UserId = cashierId,
            Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 1, UnitPrice = -3m } },
            Payment = new DtoPaymentInfo { Amount = 0m, Method = "cash" }
        });

        Assert.False(result.IsSuccess);
        Assert.False(await db.Invoices.AnyAsync(i => i.UserId == cashierId));
    }

    [Fact]
    public async Task Checkout_CashierWithinDiscountCap_IsAllowed()
    {
        string marker = $"pu3-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var (cashierId, _) = await SeedRolesAsync(db, marker);
        await SeedStockAsync(db, seed, marker, 10);
        var checkout = new clsCheckoutService(_fx.CreateContext());

        // 10% implicit discount (20 → 18) is within the cashier cap
        var result = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            UserId = cashierId,
            Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 1, UnitPrice = 18m } },
            Payment = new DtoPaymentInfo { Amount = 18m, Method = "cash" }
        });

        Assert.True(result.IsSuccess, result.Message);

        var invoice = await db.Invoices.Include(i => i.InvoiceItems).FirstAsync(i => i.InvoiceId == result.Data);
        Assert.Equal(18m, invoice.NetAmount);
        Assert.Equal(18m, invoice.InvoiceItems.Single().ItemPrice);
    }

    [Fact]
    public async Task Checkout_ManagerPriceOverride_IsAllowed()
    {
        string marker = $"pu4-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var (_, managerId) = await SeedRolesAsync(db, marker);
        await SeedStockAsync(db, seed, marker, 10);
        var checkout = new clsCheckoutService(_fx.CreateContext());

        var result = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            UserId = managerId,
            Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 2, UnitPrice = 5m } },
            Payment = new DtoPaymentInfo { Amount = 10m, Method = "cash" }
        });

        Assert.True(result.IsSuccess, result.Message);

        // Totals recalculated from the overridden price, consistently
        var invoice = await db.Invoices.Include(i => i.InvoiceItems).FirstAsync(i => i.InvoiceId == result.Data);
        Assert.Equal(10m, invoice.TotalAmount);
        Assert.Equal(10m, invoice.NetAmount);
        Assert.All(invoice.InvoiceItems, ii => Assert.Equal(5m, ii.ItemPrice));
    }

    [Fact]
    public async Task Checkout_SnapshotPriceAboveCurrentBase_AllowedForCashier()
    {
        string marker = $"pu5-{Guid.NewGuid():N}"[..12];
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var (cashierId, _) = await SeedRolesAsync(db, marker);
        await SeedStockAsync(db, seed, marker, 10);
        var checkout = new clsCheckoutService(_fx.CreateContext());

        // VULN-14 snapshot: price was raised after the item entered the cart —
        // an upward deviation is not a discount bypass, so it stays allowed.
        var result = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
        {
            UserId = cashierId,
            Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 1, UnitPrice = 25m } },
            Payment = new DtoPaymentInfo { Amount = 25m, Method = "cash" }
        });

        Assert.True(result.IsSuccess, result.Message);
        var invoice = await db.Invoices.Include(i => i.InvoiceItems).FirstAsync(i => i.InvoiceId == result.Data);
        Assert.Equal(25m, invoice.NetAmount);
    }
}
