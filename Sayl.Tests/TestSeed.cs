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

    /// <summary>
    /// دور مشترك معاد استخدامه حسب قيمة بت الصلاحية — Roles.RoleId عمود
    /// TINYINT IDENTITY (حد 255 صفًا): بذر دور جديد لكل اختبار يفيض الهوية
    /// في الرَن الكامل (Arithmetic overflow). الأدوار المتشابكة بقيمة واحدة
    /// مترادفة دلاليًا (التصريح يُقرأ من RoleValue) فإعادة الاستخدام آمنة،
    /// والإنشاء يحدث مرة واحدة لكل قيمة بت مختلفة (عدد محدود) لا لكل اختبار.
    /// ملاحظة: الدور المُعاد مستخدم للقراءة فقط (ربطه بمستخدمين). أي اختبار
    /// يعدّل حالة الدور نفسه (تعطيل/تعديل) يجب أن ينشئ دوره الخاص بعلامة فريدة.
    /// </summary>
    public static async Task<Role> GetOrCreateRoleAsync(SaylDbContext db, int roleValue)
    {
        var role = await db.Roles
            .Where(r => r.IsActive && r.RoleValue == roleValue)
            .OrderBy(r => r.RoleId)
            .FirstOrDefaultAsync();
        if (role != null) return role;

        role = new Role
        {
            Name = $"test-role-{roleValue}-{Guid.NewGuid():N}"[..32],
            RoleValue = roleValue,
            IsActive = true
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    public static async Task<TestSeed> SeedAsync(SaylDbContext db, string marker)
    {
        // Roles.RoleId عمود TINYINT IDENTITY (حد 255 صفًا): بذر دور لكل اختبار
        // يفيض الهوية في الرَن الكامل (Arithmetic overflow). نعيد استخدام دور
        // مدير نشط موجود — نفس RoleValue فلا فرق دلالي — ويُنشأ فقط في أول بذر.
        var role = await GetOrCreateRoleAsync(db, Common.Utils.AppPermissions.AdminRoleValue);

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
