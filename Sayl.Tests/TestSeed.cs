using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Tests;

/// <summary>بذر أدنى بيانات لكل اختبار: دور مدير، مستخدم، مورد، عميل، منتج مخزني.</summary>
public sealed class TestSeed
{
    public int UserId { get; init; }
    public int SupplierId { get; init; }
    public int CustomerId { get; init; }
    public int ProductId { get; init; }

    public static async Task<TestSeed> SeedAsync(SaylDbContext db, string marker)
    {
        var role = new Role { Name = $"test-admin-{marker}", RoleValue = Common.Utils.AppPermissions.AdminRoleValue, IsActive = true };
        db.Roles.Add(role);

        var user = new User
        {
            Username = $"test-user-{marker}",
            PasswordHash = "test-hash",
            Role = role,
            IsActive = true
        };
        db.Users.Add(user);

        var supplierParty = new Party { PartyType = 'S', Name = $"مورد-{marker}", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Parties.Add(supplierParty);

        var customerParty = new Party { PartyType = 'C', Name = $"عميل-{marker}", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Parties.Add(customerParty);

        var product = new Product
        {
            Name = $"منتج-{marker}",
            Type = "stock",
            BasePrice = 20m,
            CostPrice = 10m, // قديم/خارجي — يجب ألا يُستخدم كـ COGS عند وجود دفعات
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Products.Add(product);

        await db.SaveChangesAsync();

        db.Customers.Add(new Customer { PartyId = customerParty.PartyId, Balance = 0, CreditLimit = 0 });
        db.Suppliers.Add(new Supplier { PartyId = supplierParty.PartyId, Balance = 0 });
        await db.SaveChangesAsync();

        return new TestSeed
        {
            UserId = user.UserId,
            SupplierId = supplierParty.PartyId,
            CustomerId = customerParty.PartyId,
            ProductId = product.ProductId
        };
    }
}
