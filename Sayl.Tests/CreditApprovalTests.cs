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
/// اختبارات سير اعتماد المدير لتجاوز حد ائتمان العميل (Task 3).
/// السياسة: مدير نظام موثق على الخادم فقط (دخول SaylPOS نفسه: BCrypt + فحص
/// IsAdmin) يعتمد بيعًا آجلًا متجاوزًا للحد — سبب إلزامي، وسجل CreditApprovals
/// يُستهلك مرة واحدة بالضبط داخل معاملة البيع (انتقال CAS granted->consumed
/// مع إعادة فحص الربط: نفس العميل + نفس المبلغ + نفس لقطة الرصيد/الحد).
/// الاعتماد لا يعدّل CreditLimit دائمًا أبدًا، والبيع الفاشل بعد المنح يحرق
/// الاعتماد (burned) فيبقى سجل تدقيق بلا صلاحية مستقبلية.
/// </summary>
[Collection("database")]
public class CreditApprovalTests
{
    private readonly DatabaseFixture _fx;
    public CreditApprovalTests(DatabaseFixture fx) => _fx = fx;

    private const string ManagerPassword = "Manager@123";
    private const string Reason = "عميل قديم موثوق - تفويض استثنائي لمرة واحدة";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    /// <summary>بذرة + مخزون كافٍ للبيع (نفس نمط CreditLimitPolicyTests).</summary>
    private async Task<TestSeed> SeedWithStockAsync(string marker, int qty = 100, decimal unitCost = 5m)
    {
        await using var db = _fx.CreateContext();
        var seed = await TestSeed.SeedAsync(db, marker);
        var purchase = new clsPurchaseService(db);
        var r = await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = $"B-{marker}", Quantity = qty, UnitCost = unitCost } }
        });
        Assert.True(r.IsSuccess, r.Message);
        return seed;
    }

    /// <summary>مستخدم اختبار حقيقي بكلمة مرور BCrypt صالحة — للمصادقة عبر
    /// LoginAsync. أدوار ثابتة معاد استخدامها (حصر RoleId TINYINT).</summary>
    private async Task<(int UserId, string Username)> CreateUserAsync(SaylDbContext db, string marker, int roleValue)
    {
        var role = await TestSeed.GetOrCreateRoleAsync(db, roleValue);
        string username = $"ta-{marker}-{Guid.NewGuid():N}";
        var user = new User
        {
            Username = username.Length > 50 ? username[..50] : username, // UQ_Users_Username: NVARCHAR(50)
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(ManagerPassword),
            Role = role,
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user.UserId, user.Username);
    }

    /// <summary>خدمة الاعتماد على سياق الاختبار (نفس نمط بناء الخدمات اليدوي).</summary>
    private static clsCreditApprovalService ApprovalService(SaylDbContext db)
        => new(db, new clsUsersService(db));

    /// <summary>بيع آجل 1 وحدة بسعر معين — دفعة صفر (كل الصافي دَين).</summary>
    private static DtoCheckoutRequest SaleRequest(TestSeed seed, int? customerId, decimal unitPrice, int? approvalId = null)
        => new()
        {
            CustomerId = customerId,
            UserId = seed.UserId,
            Items = { new DtoCheckoutItem { ProductId = seed.ProductId, ProductName = "x", Quantity = 1, UnitPrice = unitPrice } },
            Payment = new DtoPaymentInfo { Amount = 0m, Method = "cash", PaymentDate = Today },
            CreditApprovalId = approvalId
        };

    /// <summary>ضبط حد ائتمان العميل مباشرة.</summary>
    private static async Task SetLimitAsync(SaylDbContext db, int customerId, decimal limit)
        => await db.Customers.Where(c => c.PartyId == customerId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.CreditLimit, limit));

    private async Task<decimal> BalanceOfAsync(SaylDbContext db, int customerId)
        => await db.Customers.AsNoTracking().Where(c => c.PartyId == customerId)
            .Select(c => c.Balance ?? 0m).SingleAsync();

    private async Task<CreditApproval?> ApprovalRowAsync(SaylDbContext db, int approvalId)
        => await db.CreditApprovals.AsNoTracking().SingleOrDefaultAsync(a => a.ApprovalId == approvalId);

    /// <summary>قراءة صف الاعتماد حصرًا (يجب أن يكون موجودًا).</summary>
    private async Task<CreditApproval> ApprovalMustExistAsync(SaylDbContext db, int approvalId)
        => await ApprovalRowAsync(db, approvalId) ?? throw new InvalidOperationException($"approval {approvalId} missing");

    /// <summary>مطالبة البيع المتجاوز بالحد بنتيجة اعتماد منظمة.</summary>
    private static CreditApprovalRequiredResult<int> AssertApprovalRequired(Result<int> result)
    {
        Assert.False(result.IsSuccess);
        var approval = Assert.IsType<CreditApprovalRequiredResult<int>>(result);
        Assert.NotNull(approval.ApprovalInfo);
        return approval;
    }

    /// <summary>سير كامل: رفض الحد -> نافذة (محاكاة) -> منح -> إعادة محاولة.
    /// يُرجع نتيجة إعادة المحاولة ومعرف الاعتماد.</summary>
    private async Task<(Result<int> Retry, int ApprovalId)> GrantAndRetryAsync(
        SaylDbContext db, TestSeed seed, CreditApprovalRequiredResult<int> approvalRequired,
        string username, string password, string reason, DtoCheckoutRequest? customRequest = null)
    {
        var grant = await ApprovalService(db).ApproveCreditSaleAsync(
            approvalRequired.ApprovalInfo!, username, password, reason);
        Assert.True(grant.IsSuccess, grant.Message);
        var request = customRequest ?? SaleRequest(seed, seed.CustomerId, approvalRequired.ApprovalInfo!.NewCreditAmount);
        request.CreditApprovalId = grant.Data;
        var retry = await new clsCheckoutService(db).ProcessCheckoutAsync(request);
        return (retry, grant.Data);
    }

    // ══ 1) المدير يعتمد بيعًا متجاوزًا صالحًا: النجاح والرصيد والاستهلاك ══
    [Fact]
    public async Task ManagerApprovesValidOverLimitSale_Succeeds_AndConsumesApproval()
    {
        var seed = await SeedWithStockAsync($"ca1-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);

        var first = AssertApprovalRequired(await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 150m)));
        var manager = await CreateUserAsync(db, "ca1", AppPermissions.AdminRoleValue);

        var (retry, approvalId) = await GrantAndRetryAsync(db, seed, first, manager.Username, ManagerPassword, Reason);

        Assert.True(retry.IsSuccess, retry.Message);
        Assert.Equal(150m, await BalanceOfAsync(db, seed.CustomerId));

        // الاستهلاك الدقيق: consumed + فاتورة البيع نفسها + لحظة الاستهلاك
        var row = await ApprovalMustExistAsync(db, approvalId);
        Assert.Equal("consumed", row.Status);
        Assert.Equal(retry.Data, row.ConsumedByInvoiceId);
        Assert.NotNull(row.ConsumedAtUtc);
        Assert.Equal(manager.UserId, row.ManagerUserId);
    }

    // ══ 2) الكاشير لا يعتمد: رفض + لا سجل اعتماد ══
    [Fact]
    public async Task CashierCannotApprove()
    {
        var seed = await SeedWithStockAsync($"ca2-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);
        var manager = await CreateUserAsync(db, "ca2", AppPermissions.CashierRoleValue);

        var grant = await ApprovalService(db).ApproveCreditSaleAsync(
            OverLimitInfo(seed.CustomerId), manager.Username, ManagerPassword, Reason);

        Assert.False(grant.IsSuccess);
        Assert.Contains("غير مصرَّح", grant.Message);
        Assert.Equal(0, await db.CreditApprovals.CountAsync(a => a.ManagerUserId == manager.UserId));
        Assert.Equal(1, await db.SystemLogs.CountAsync(l => l.Action == "CreditApprovalDenied" && l.UserId == manager.UserId));
    }

    // ══ 3) المحاسب لا يعتمد (وأي دور مخصص) ══
    [Fact]
    public async Task AccountantAndCustomRole_CannotApprove()
    {
        var seed = await SeedWithStockAsync($"ca3-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        var accountant = await CreateUserAsync(db, "ca3a", AppPermissions.AccountantRoleValue);
        var custom = await CreateUserAsync(db, "ca3b", AppPermissions.CustomRoleCeiling);

        var g1 = await ApprovalService(db).ApproveCreditSaleAsync(
            OverLimitInfo(seed.CustomerId), accountant.Username, ManagerPassword, Reason);
        var g2 = await ApprovalService(db).ApproveCreditSaleAsync(
            OverLimitInfo(seed.CustomerId), custom.Username, ManagerPassword, Reason);

        Assert.False(g1.IsSuccess);
        Assert.False(g2.IsSuccess);
        Assert.Equal(0, await db.CreditApprovals.CountAsync(a => a.ManagerUserId == accountant.UserId || a.ManagerUserId == custom.UserId));
    }

    // ══ 4) بيانات مدير غير صالحة: رفض مصادقة بلا أي سجل ══
    [Fact]
    public async Task InvalidManagerCredentials_AreRejected()
    {
        var seed = await SeedWithStockAsync($"ca4-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        var manager = await CreateUserAsync(db, "ca4", AppPermissions.AdminRoleValue);

        var grant = await ApprovalService(db).ApproveCreditSaleAsync(
            OverLimitInfo(seed.CustomerId), manager.Username, "Wrong@123", Reason);

        Assert.False(grant.IsSuccess);
        Assert.Contains("فشل التحقق", grant.Message);
        Assert.Equal(0, await db.CreditApprovals.CountAsync(a => a.CustomerId == seed.CustomerId));
    }

    // ══ 5) السبب الفارغ/القصير/الطويل مرفوض ══
    [Fact]
    public async Task InvalidReason_IsRejected()
    {
        var seed = await SeedWithStockAsync($"ca5-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        var manager = await CreateUserAsync(db, "ca5", AppPermissions.AdminRoleValue);
        var info = OverLimitInfo(seed.CustomerId);
        var svc = ApprovalService(db);

        Assert.False((await svc.ApproveCreditSaleAsync(info, manager.Username, ManagerPassword, "")).IsSuccess);
        Assert.False((await svc.ApproveCreditSaleAsync(info, manager.Username, ManagerPassword, "   ")).IsSuccess);
        Assert.False((await svc.ApproveCreditSaleAsync(info, manager.Username, ManagerPassword, "ر")).IsSuccess);
        Assert.False((await svc.ApproveCreditSaleAsync(info, manager.Username, ManagerPassword, new string('ر', 501))).IsSuccess);
        Assert.Equal(0, await db.CreditApprovals.CountAsync(a => a.CustomerId == seed.CustomerId));
    }

    // ══ 6) بيانات الاعتماد تُخزَّن كاملة وصحيحة ══
    [Fact]
    public async Task ApprovalMetadata_IsPersistedCorrectly()
    {
        var seed = await SeedWithStockAsync($"ca6-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);
        var manager = await CreateUserAsync(db, "ca6", AppPermissions.AdminRoleValue);

        var before = DateTime.UtcNow.AddSeconds(-1);
        var grant = await ApprovalService(db).ApproveCreditSaleAsync(
            new CreditApprovalRequiredInfo
            {
                CustomerId = seed.CustomerId,
                CurrentOutstanding = 40m,
                CreditLimit = 100m,
                NewCreditAmount = 90m,
                ProjectedOutstanding = 130m,
                ExcessAmount = 30m
            }, manager.Username, ManagerPassword, Reason);
        Assert.True(grant.IsSuccess, grant.Message);

        var row = await ApprovalMustExistAsync(db, grant.Data);
        Assert.Equal(seed.CustomerId, row.CustomerId);
        Assert.Equal(manager.UserId, row.ManagerUserId);
        Assert.Equal(40m, row.CurrentOutstanding);
        Assert.Equal(100m, row.CreditLimit);
        Assert.Equal(90m, row.NewCreditAmount);
        Assert.Equal(130m, row.ProjectedOutstanding);
        Assert.Equal(30m, row.ExcessAmount);
        Assert.Equal(Reason, row.Reason);
        Assert.Equal("granted", row.Status);
        Assert.Null(row.ConsumedByInvoiceId);
        Assert.Null(row.ConsumedAtUtc);
        Assert.True(row.ApprovedAtUtc >= before && row.ApprovedAtUtc <= DateTime.UtcNow.AddSeconds(1));

        // تدقيق المنح: هوية المدير + السبب (بلا أسرار أبدًا)
        Assert.Equal(1, await db.SystemLogs.CountAsync(l => l.Action == "CreditApprovalGranted" && l.UserId == manager.UserId));
    }

    // ══ 7) الاعتماد يصرّح بعملية بيع واحدة بالضبط ══
    [Fact]
    public async Task Approval_AuthorizesExactlyOneCheckout()
    {
        var seed = await SeedWithStockAsync($"ca7-{Guid.NewGuid():N}"[..12], qty: 100);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);

        var first = AssertApprovalRequired(await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 150m)));
        var manager = await CreateUserAsync(db, "ca7", AppPermissions.AdminRoleValue);
        var (retry, approvalId) = await GrantAndRetryAsync(db, seed, first, manager.Username, ManagerPassword, Reason);
        Assert.True(retry.IsSuccess, retry.Message);

        // محاولة ثانية (نفس الاعتماد): البيع الجديد يتجاوز الحد مجددًا — مرفوض
        var second = AssertApprovalRequired(await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 150m, approvalId)));
        Assert.Equal(150m, await BalanceOfAsync(db, seed.CustomerId)); // دَين واحد فقط
        Assert.Equal(1, await db.Invoices.CountAsync(i => i.PartyId == seed.CustomerId && i.Type == enInvoicesType.sale));

        // صف الاعتماد لم يتغير: مستهلك بالفاتورة الأولى وحدها
        var row = await ApprovalMustExistAsync(db, approvalId);
        Assert.Equal("consumed", row.Status);
        Assert.Equal(retry.Data, row.ConsumedByInvoiceId);
        Assert.Contains("موافقة مدير جديدة", second.Message);
    }

    /// <summary>حمولة تجاوز ثابتة لفحص بوابة الاعتماد (بلا بيع فعلي).</summary>
    private static CreditApprovalRequiredInfo OverLimitInfo(int customerId)
        => new()
        {
            CustomerId = customerId,
            CurrentOutstanding = 0m,
            CreditLimit = 100m,
            NewCreditAmount = 150m,
            ProjectedOutstanding = 150m,
            ExcessAmount = 50m
        };

    // ══ 9) اعتماد عميل لا يصرّح بعميل آخر ══
    [Fact]
    public async Task Approval_ForOneCustomer_CannotAuthorizeAnother()
    {
        var seedA = await SeedWithStockAsync($"ca9a-{Guid.NewGuid():N}"[..12]);
        var seedB = await SeedWithStockAsync($"ca9b-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seedA.CustomerId, 100m);
        await SetLimitAsync(db, seedB.CustomerId, 100m);
        var manager = await CreateUserAsync(db, "ca9", AppPermissions.AdminRoleValue);

        // اعتماد للعميل A (آجل 150 فوق حده 100)
        var grant = await ApprovalService(db).ApproveCreditSaleAsync(OverLimitInfo(seedA.CustomerId), manager.Username, ManagerPassword, Reason);
        Assert.True(grant.IsSuccess, grant.Message);

        // محاولة استخدامه لعميل B: الربط مرفوض — B تحصل على نتيجة اعتماد جديدة،
        // والمحاولة الفاشلة تُحرق اعتماد A (لا يعود صالحًا لمحاولة أخرى)
        var retryB = AssertApprovalRequired(await new clsCheckoutService(db)
            .ProcessCheckoutAsync(SaleRequest(seedB, seedB.CustomerId, 150m, grant.Data)));
        Assert.Equal(seedB.CustomerId, retryB.ApprovalInfo!.CustomerId);
        Assert.Equal(0m, await BalanceOfAsync(db, seedB.CustomerId));

        // اعتماد A أُحرق بلا استهلاك: لم يقابله بيع A ولن يستفاد منه مستقبلًا
        var row = await ApprovalMustExistAsync(db, grant.Data);
        Assert.Equal("burned", row.Status);
        Assert.Null(row.ConsumedByInvoiceId);
    }

    // ══ 10) اعتماد مبلغ لا يصرّح بمبلغ آخر ══
    [Fact]
    public async Task Approval_ForOneAmount_CannotAuthorizeDifferentAmount()
    {
        var seed = await SeedWithStockAsync($"ca10-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);
        var manager = await CreateUserAsync(db, "ca10", AppPermissions.AdminRoleValue);

        // اعتماد لآجل 150 — ثم محاولة بيع آجله 120 فقط
        var grant = await ApprovalService(db).ApproveCreditSaleAsync(OverLimitInfo(seed.CustomerId), manager.Username, ManagerPassword, Reason);
        Assert.True(grant.IsSuccess, grant.Message);

        var retry = AssertApprovalRequired(await new clsCheckoutService(db)
            .ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 120m, grant.Data)));
        Assert.Equal(120m, retry.ApprovalInfo!.NewCreditAmount);
        Assert.Equal(0m, await BalanceOfAsync(db, seed.CustomerId));
        Assert.Equal("burned", (await ApprovalMustExistAsync(db, grant.Data)).Status);
    }

    // ══ 11) تغيّر الرصيد بعد الاعتماد = اعتماد جديد مطلوب ══
    [Fact]
    public async Task BalanceChanged_AfterApproval_RequiresFreshApproval()
    {
        var seed = await SeedWithStockAsync($"ca11-{Guid.NewGuid():N}"[..12], qty: 100);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);
        var manager = await CreateUserAsync(db, "ca11", AppPermissions.AdminRoleValue);

        var first = AssertApprovalRequired(await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 150m)));
        var grant = await ApprovalService(db).ApproveCreditSaleAsync(first.ApprovalInfo!, manager.Username, ManagerPassword, Reason);
        Assert.True(grant.IsSuccess, grant.Message);

        // تغيّر موقف العميل بعد الاعتماد: بيع آجل صغير داخل الحد (20) رفع الرصيد
        var small = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 20m));
        Assert.True(small.IsSuccess, small.Message);

        // إعادة المحاولة بالاعتماد القديم: لقطة الرصيد (0) لم تعد مطابقة (20) — رفض،
        // والمحاولة الفاشلة تُحرق الاعتماد (سياسة الفشل: لا صلاحية لمحاولة تالية)
        var retry = AssertApprovalRequired(await new clsCheckoutService(db)
            .ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 150m, grant.Data)));
        Assert.Equal(20m, retry.ApprovalInfo!.CurrentOutstanding);
        Assert.Equal(20m, await BalanceOfAsync(db, seed.CustomerId)); // البيع الصغير فقط
        Assert.Equal("burned", (await ApprovalMustExistAsync(db, grant.Data)).Status);
    }

    // ══ 12) فشل البيع بعد الاعتماد: لا فاتورة ولا دَين ══
    [Fact]
    public async Task FailedCheckout_AfterApproval_CreatesNoDebtOrInvoice()
    {
        var seed = await SeedWithStockAsync($"ca12-{Guid.NewGuid():N}"[..12], qty: 1); // وحدة واحدة فقط
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);
        var manager = await CreateUserAsync(db, "ca12", AppPermissions.AdminRoleValue);

        // اعتماد بيع 150 — ثم إعادة المحاولة بكمية أكبر من المخزون: فشل بعد المنح
        var grant = await ApprovalService(db).ApproveCreditSaleAsync(OverLimitInfo(seed.CustomerId), manager.Username, ManagerPassword, Reason);
        Assert.True(grant.IsSuccess, grant.Message);

        var request = SaleRequest(seed, seed.CustomerId, 150m, grant.Data);
        request.Items[0].Quantity = 5; // مخزون 1 — فشل تحقق مخزون بعد بناء الفاتورة
        var retry = await new clsCheckoutService(db).ProcessCheckoutAsync(request);

        Assert.False(retry.IsSuccess);
        Assert.IsNotType<CreditApprovalRequiredResult<int>>(retry);
        Assert.Equal(0m, await BalanceOfAsync(db, seed.CustomerId));
        Assert.Equal(0, await db.Invoices.CountAsync(i => i.PartyId == seed.CustomerId && i.Type == enInvoicesType.sale));
        Assert.Equal(0, await db.InvoiceItems.CountAsync(ii => ii.Invoice.PartyId == seed.CustomerId));
    }

    // ══ 13) فشل البيع بعد الاعتماد: سجل «مُنح ولم يُستهلك» دقيق وغير قابل للاستخدام ══
    [Fact]
    public async Task FailedCheckout_AfterApproval_BurnsApproval_WithAuditRecord()
    {
        var seed = await SeedWithStockAsync($"ca13-{Guid.NewGuid():N}"[..12], qty: 1);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);
        var manager = await CreateUserAsync(db, "ca13", AppPermissions.AdminRoleValue);

        var grant = await ApprovalService(db).ApproveCreditSaleAsync(OverLimitInfo(seed.CustomerId), manager.Username, ManagerPassword, Reason);
        Assert.True(grant.IsSuccess, grant.Message);

        var request = SaleRequest(seed, seed.CustomerId, 150m, grant.Data);
        request.Items[0].Quantity = 5;
        var retry = await new clsCheckoutService(db).ProcessCheckoutAsync(request);
        Assert.False(retry.IsSuccess);

        // الحرق: حالة صريحة بلا فاتورة — سجل «مُنح ولم يُستهلك» يبقى للمراجعة
        var row = await ApprovalMustExistAsync(db, grant.Data);
        Assert.Equal("burned", row.Status);
        Assert.Null(row.ConsumedByInvoiceId);
        Assert.Null(row.ConsumedAtUtc);
        Assert.Equal(Reason, row.Reason); // السبب وهوية المدير بقيا في السجل
        Assert.Equal(manager.UserId, row.ManagerUserId);
        // تدقيق الحرق مرتبط بهذا الاعتماد حصرًا (معرف الاعتماد في نص السجل)
        Assert.Equal(1, await db.SystemLogs.CountAsync(l =>
            l.Action == "CreditApprovalBurned" && l.Details!.Contains($"#{grant.Data} ")));

        // لا صلاحية مستقبلية: نفس البيع (مخزون مُتاح بعد إعادة التزويد) بالاعتماد المحروق مرفوض
        await new clsPurchaseService(db).CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
        {
            SupplierId = seed.SupplierId,
            UserId = seed.UserId,
            Items = { new DtoPurchaseItem { ProductId = seed.ProductId, BatchNumber = $"B2-{seed.ProductId}", Quantity = 50, UnitCost = 5m } }
        });
        var reuse = AssertApprovalRequired(await new clsCheckoutService(db)
            .ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 150m, grant.Data)));
        Assert.Contains("لم يعد صالحًا", reuse.Message);
        Assert.Equal("burned", (await ApprovalMustExistAsync(db, grant.Data)).Status);
    }

    // ══ 14) البيع داخل الحد: بلا اعتماد إطلاقًا ══
    [Fact]
    public async Task CheckoutWithinLimit_DoesNotRequestApproval()
    {
        var seed = await SeedWithStockAsync($"ca14-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);

        var r = await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 60m));

        Assert.True(r.IsSuccess, r.Message);
        Assert.IsNotType<CreditApprovalRequiredResult<int>>(r);
        Assert.Equal(0, await db.CreditApprovals.CountAsync(a => a.CustomerId == seed.CustomerId));
    }

    // ══ 15) حد صفري: بيع الائتمان يتطلب الاعتماد — ومعه ينجح ══
    [Fact]
    public async Task ZeroLimitCustomer_RequiresApproval_ThenSucceedsWithIt()
    {
        var seed = await SeedWithStockAsync($"ca15-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 0m);
        var manager = await CreateUserAsync(db, "ca15", AppPermissions.AdminRoleValue);

        var first = AssertApprovalRequired(await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 1m)));
        var (retry, approvalId) = await GrantAndRetryAsync(db, seed, first, manager.Username, ManagerPassword, Reason);

        Assert.True(retry.IsSuccess, retry.Message);
        Assert.Equal(1m, await BalanceOfAsync(db, seed.CustomerId));
        Assert.Equal("consumed", (await ApprovalMustExistAsync(db, approvalId)).Status);
    }

    // ══ 16) الاعتماد لا يعدّل حد العميل الدائم أبدًا ══
    [Fact]
    public async Task Approval_DoesNotModifyPermanentCreditLimit()
    {
        var seed = await SeedWithStockAsync($"ca16-{Guid.NewGuid():N}"[..12]);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);
        var manager = await CreateUserAsync(db, "ca16", AppPermissions.AdminRoleValue);

        var first = AssertApprovalRequired(await new clsCheckoutService(db).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 150m)));
        var (retry, _) = await GrantAndRetryAsync(db, seed, first, manager.Username, ManagerPassword, Reason);
        Assert.True(retry.IsSuccess, retry.Message);

        Assert.Equal(100m, await db.Customers.AsNoTracking()
            .Where(c => c.PartyId == seed.CustomerId).Select(c => c.CreditLimit ?? 0m).SingleAsync());
    }

    // ══ 17) طلبان متوازيان لا يستهلكان نفس الاعتماد ══
    [Fact]
    public async Task ConcurrentCheckouts_CannotConsumeTheSameApproval()
    {
        var seed = await SeedWithStockAsync($"ca17-{Guid.NewGuid():N}"[..12], qty: 10);
        await using var db = _fx.CreateContext();
        await SetLimitAsync(db, seed.CustomerId, 100m);
        var manager = await CreateUserAsync(db, "ca17", AppPermissions.AdminRoleValue);

        // اعتماد واحد لآجل 150 (فائض 50 فوق الحد 100) — بيعان متوازيان بآجل 150
        var grant = await ApprovalService(db).ApproveCreditSaleAsync(
            new CreditApprovalRequiredInfo
            {
                CustomerId = seed.CustomerId,
                CurrentOutstanding = 0m,
                CreditLimit = 100m,
                NewCreditAmount = 150m,
                ProjectedOutstanding = 150m,
                ExcessAmount = 50m
            }, manager.Username, ManagerPassword, Reason);
        Assert.True(grant.IsSuccess, grant.Message);

        // لقب العميل UPDLOCK يرتّب المتسابقين (الثاني يقرأ الرصيد بعد التزام
        // الأول) وCAS على صف الاعتماد يفوز مرة واحدة: استهلاك واحد بالضبط
        // والثاني يُرفض (مستهلك أو لقطة لم تعد مطابقة).
        var t1 = Task.Run(async () =>
        {
            await using var c = _fx.CreateContext();
            return await new clsCheckoutService(c).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 150m, grant.Data));
        });
        var t2 = Task.Run(async () =>
        {
            await using var c = _fx.CreateContext();
            return await new clsCheckoutService(c).ProcessCheckoutAsync(SaleRequest(seed, seed.CustomerId, 150m, grant.Data));
        });
        var results = await Task.WhenAll(t1, t2);

        int successes = results.Count(r => r.IsSuccess);
        Assert.Equal(1, successes); // استهلاك واحد بالضبط
        Assert.Equal(1, await db.Invoices.CountAsync(i => i.PartyId == seed.CustomerId && i.Type == enInvoicesType.sale));
        Assert.Equal(150m, await BalanceOfAsync(db, seed.CustomerId));

        // صف الاعتماد: مستهلك بفاتورة الفائز الوحيد
        var consumedByInvoice = await db.CreditApprovals.AsNoTracking()
            .Where(a => a.ApprovalId == grant.Data).Select(a => a.ConsumedByInvoiceId).SingleAsync();
        Assert.Equal(results.First(r => r.IsSuccess).Data, consumedByInvoice);
    }
}
