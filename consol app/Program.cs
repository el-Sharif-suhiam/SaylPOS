using Common.DTOs;
using Common.DTOs.RequestModels;
using Common.DTOs.ViewModel;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using DependencyInjection;
using Sayl.Core.Services;
using Sayl.Core.Models;

namespace consol_app
{
    /// <summary>
    /// E2E test harness: runs full usage cycles against the real SaylDB
    /// exactly like the UI would call the services.
    /// </summary>
    internal class Program
    {
        private static IServiceProvider Services = default!;
        private static int _pass, _fail;
        private static readonly List<string> _failures = new();

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            // المثيل المسماة SAYL — المثبت ينشئها (SaylInstance = 'SAYL' في setup.iss)
            // والافتراضي '.' قد لا يحمل SaylDB على جهاز التطوير.
            Services = new ServiceCollection()
                .AddSaylServices("Server=.\\SAYL;Database=SaylDB;Trusted_Connection=True;TrustServerCertificate=True;")
                .BuildServiceProvider();

            // مسبار التزويد: دورات الهارنس تستهلك مخزون المنتج 1 دورة بعد دورة
            // (بيع/تعليق/مرتجع...) — نعيد 500 وحدة بدمغة وقتية فريدة قبل كل جولة
            // كي تبقى الاختبارات قابلة للتكرار إلى ما لا نهاية (idempotent).
            using (var ws = Scope())
            {
                var stock = Svc<clsStockService>(ws);
                var db = Svc<Sayl.Core.Data.SaylDbContext>(ws);
                // ترقية المخطط idempotent أولًا: أي قاعدة قائمة (كقاعدة التطوير)
                // تكتسب عمود MustChangePassword تلقائيًا قبل أول استعلام.
                await Svc<clsUsersService>(ws).EnsureSchemaUpgradesAsync();
                // جدول حجوزات الفواتير المعلقة (StockReservations) — idempotent كذلك
                await Svc<clsCheckoutService>(ws).EnsureReservationSchemaAsync();
                var admin = await db.Users
                    .FirstOrDefaultAsync(u => u.Username == "admin" && u.IsActive);
                await stock.ReceiveStockAsync(new DtoStockReceiveRequest
                {
                    ProductId = 1,
                    BatchNumber = "HARNESS-" + DateTime.Now.Ticks,
                    Quantity = 500
                }, admin?.UserId ?? 0);
            }

            await AuthCycle();
            var (sessionId, cashierId) = await SessionAndSaleCycle();
            await QuickAddPartyCycle();
            if (sessionId > 0)
            {
                await SuspendResumeCancelCycle(sessionId, cashierId);
                await ReservationOwnershipCycle();
                await ReturnCycle();
                await CashDeskCycle(sessionId);
            }
            await FinancialGuardsAttackCycle(sessionId);
            await CreditRulesCycle();
            await PermissionRulesCycle();
            await BitmaskPermissionsCycle();
            await ReportsCycle();
            await SettingsBackupCycle();
            await CrudSmokeCycle();
            await AdjustBatchConsistencyCycle();
            await OwnerRecoveryCycle();
            await PaymentAllocationCycle();
            await RefundPlanCycle();

            Console.WriteLine();
            Console.WriteLine($"================ RESULT: {_pass} PASSED / {_fail} FAILED ================");
            foreach (var f in _failures)
                Console.WriteLine("FAIL: " + f);
        }

        // ───────────────── helpers ─────────────────
        private static void Check(string name, bool condition, string detail = "")
        {
            if (condition) { _pass++; Console.WriteLine($"[PASS] {name}"); }
            else { _fail++; _failures.Add($"{name} {detail}"); Console.WriteLine($"[FAIL] {name} {detail}"); }
        }

        private static IServiceScope Scope() => Services.CreateScope();
        private static T Svc<T>(IServiceScope s) where T : notnull => s.ServiceProvider.GetRequiredService<T>();

        /// <summary>قراءة تحقق من سياق معزول تمامًا (بلا تتبع مشترك مع الخدمات).</summary>
        private static async Task<T> VerifyAsync<T>(Func<Sayl.Core.Data.SaylDbContext, Task<T>> query)
        {
            using var vs = Scope();
            var vdb = Svc<Sayl.Core.Data.SaylDbContext>(vs);
            return await query(vdb);
        }

        // ───────────────── 1. Auth ─────────────────
        private static async Task AuthCycle()
        {
            Console.WriteLine("--- AUTH ---");
            using var s = Scope();
            var users = Svc<clsUsersService>(s);
            var db = Svc<Sayl.Core.Data.SaylDbContext>(s);

            // VULN-47: لا يوجد حسابات افتراضية معروفة — الهارنس ينشئ حساباته الصالحة أولًا
            var adminRole = await db.Roles.FirstAsync(r => r.RoleValue == AppPermissions.AdminRoleValue);
            var cashierRole = await db.Roles.FirstAsync(r => r.RoleValue == AppPermissions.CashierRoleValue);

            User? adminUser;
            var freshCreated = !await db.Users.AnyAsync(u => u.IsActive);
            if (freshCreated)
            {
                // أول تشغيل / قاعدة نظيفة: ننشئ المدير الافتراضي (يفرض تغيير كلمة
                // المرور) ثم الكاشير. VULN-47: لا أولد أي حسابات معروفة في SQL.
                var ownerResult = await users.EnsureDefaultAdminAsync();
                if (!ownerResult.IsSuccess)
                    throw new Exception("EnsureDefaultAdmin failed: " + ownerResult.Message);

                adminUser = await db.Users.FirstAsync(u => u.UserId == ownerResult.Data!.UserId);

                var ownerActive = await VerifyAsync(async vdb => await vdb.Users
                    .Where(u => u.UserId == ownerResult.Data!.UserId)
                    .Select(u => u.IsActive)
                    .SingleAsync());
                Check("created owner is active in DB", ownerActive);

                // مسار إجبار التغيير كاملًا: العلامة في قاعدة البيانات، وتظهر عند
                // الدخول، ويقبل التغيير ويمسحها، ثم نعيد كلمة المرور الأصلية
                // كي تبقى الجولة قابلة للتكرار.
                var freshFlag = await VerifyAsync(async vdb => await vdb.Users
                    .Where(u => u.UserId == ownerResult.Data!.UserId)
                    .Select(u => u.MustChangePassword)
                    .SingleAsync());
                Check("default admin forces password change (DB flag)", freshFlag);

                var forcedLogin = await users.LoginAsync("admin", "Admin@123");
                Check("forced-change login succeeds", forcedLogin.IsSuccess, forcedLogin.Message);
                Check("forced-change flag surfaced on login", forcedLogin.Data!.MustChangePassword,
                    forcedLogin.Data.MustChangePassword ? "" : "flag missing on UserVM");

                var pwdChange = await users.ChangePasswordAsync("admin", "Admin@123", "ForcedChange#9");
                Check("forced password change accepted", pwdChange.IsSuccess, pwdChange.Message);

                var flagAfterChange = await VerifyAsync(async vdb => await vdb.Users
                    .Where(u => u.UserId == ownerResult.Data!.UserId)
                    .Select(u => u.MustChangePassword)
                    .SingleAsync());
                Check("forced-change flag cleared after change", !flagAfterChange);

                var restored = await users.ResetPasswordAsync(ownerResult.Data.UserId, "Admin@123", ownerResult.Data.UserId);
                Check("default password restored for reruns", restored.IsSuccess, restored.Message);

                var cashierResult = await users.AddNewUserAsync(
                    new NewUserRequest("cashier1", "Cashier123", cashierRole.RoleId, null),
                    actorUserId: adminUser.UserId);
                if (!cashierResult.IsSuccess)
                    throw new Exception("Add cashier failed: " + cashierResult.Message);

                var cashierActive = await VerifyAsync(async vdb => await vdb.Users
                    .Where(u => u.Username == "cashier1")
                    .Select(u => u.IsActive)
                    .SingleAsync());
                Check("created cashier is active in DB", cashierActive);
            }
            else
            {
                adminUser = await db.Users.FirstAsync(u => u.Username == "admin");
                var cashierUser = await db.Users.FirstOrDefaultAsync(u => u.Username == "cashier1");
                if (cashierUser == null)
                {
                    // قاعدة تطوير فيها مدير بلا كاشير (أو كاشير معطّل حُذف) — ننشئه
                    // بدل انهيار الهارنس بـ Sequence contains no elements
                    var mkCashier = await users.AddNewUserAsync(
                        new NewUserRequest("cashier1", "Cashier123", cashierRole.RoleId, null),
                        actorUserId: adminUser.UserId);
                    if (!mkCashier.IsSuccess)
                        throw new Exception("Add cashier failed: " + mkCashier.Message);
                    cashierUser = await db.Users.FirstAsync(u => u.Username == "cashier1");
                }
                await users.ResetPasswordAsync(cashierUser.UserId, "Cashier123", adminUser.UserId);
                // إعادة كلمة مرور المدير الافتراضية أيضًا — قواعد التطوير القديمة قد
                // تحمل كلمة غير معروفة فينهار باقي الدورات عند الدخول
                await users.ResetPasswordAsync(adminUser.UserId, "Admin@123", adminUser.UserId);

                // VULN-47: لا إنشاء صامت لمدير افتراضي على نظام له مستخدمون أصلًا
                var refusedDefault = await users.EnsureDefaultAdminAsync();
                Check("ensure-default-admin refused when users exist", !refusedDefault.IsSuccess, refusedDefault.Message);
            }

            if (adminUser == null)
                throw new Exception("Admin user could not be resolved.");

            var adminLogin = await users.LoginAsync("admin", "Admin@123");
            Check("admin login", adminLogin.IsSuccess);

            var cashierLogin = await users.LoginAsync("cashier1", "Cashier123");
            Check("cashier1 login (after reset)", cashierLogin.IsSuccess, cashierLogin.Message);

            var bad = await users.LoginAsync("admin", "definitely-wrong-999");
            Check("wrong password rejected", !bad.IsSuccess);

            var unknown = await users.LoginAsync("no-such-user", "whatever123");
            Check("unknown user rejected", !unknown.IsSuccess);
        }

        // ───────────────── 1b. Owner recovery after reinstall ─────────────────
        // سيناريو إعادة التركيب فوق قاعدة قديمة: كل المستخدمين معطّلون (admin
        // ضمنهم) بعد فك التثبيت/الترقية. EnsureDefaultAdmin يجب أن يعيد تفعيل
        // admin بدل الفشل بـ«اسم المستخدم موجود مسبقًا». نأخذ لقطة ونستعيدها
        // في finally كي تبقى القاعدة صالحة لباقي الجولات وللدخول العادي.
        private static async Task OwnerRecoveryCycle()
        {
            Console.WriteLine("--- OWNER RECOVERY (reinstall over old DB) ---");
            List<User> snapshot;
            User? admin;
            using (var s = Scope())
            {
                var db = Svc<Sayl.Core.Data.SaylDbContext>(s);
                snapshot = await db.Users.AsNoTracking().ToListAsync();
                admin = snapshot.FirstOrDefault(u => u.Username == "admin");
            }

            if (admin == null)
            {
                Check("owner recovery requires an existing admin row", false);
                return;
            }

            try
            {
                using (var s = Scope())
                {
                    var db = Svc<Sayl.Core.Data.SaylDbContext>(s);
                    await db.Users.ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
                }

                using (var s = Scope())
                {
                    var users = Svc<clsUsersService>(s);

                    var recovery = await users.EnsureDefaultAdminAsync();
                    Check("owner recovery reactivates inactive admin", recovery.IsSuccess, recovery.Message);
                    if (recovery.IsSuccess)
                    {
                        Check("owner recovery restores admin role",
                            recovery.Data!.RoleValue == AppPermissions.AdminRoleValue);
                        Check("owner recovery forces password change", recovery.Data.MustChangePassword);

                        var login = await users.LoginAsync("admin", "Admin@123");
                        Check("owner recovery default login works", login.IsSuccess, login.Message);
                    }

                    var refused = await users.EnsureDefaultAdminAsync();
                    Check("owner recovery refused once admin is active again", !refused.IsSuccess, refused.Message);
                }

                using (var s = Scope())
                {
                    var db = Svc<Sayl.Core.Data.SaylDbContext>(s);
                    Check("owner recovery audit entry written",
                        await db.SystemLogs.AnyAsync(l => l.Action == "OwnerRecovered" && l.UserId == admin.UserId));
                }
            }
            finally
            {
                using var s = Scope();
                var db = Svc<Sayl.Core.Data.SaylDbContext>(s);
                foreach (var u in snapshot)
                {
                    await db.Users.Where(x => x.UserId == u.UserId).ExecuteUpdateAsync(set => set
                        .SetProperty(x => x.IsActive, u.IsActive)
                        .SetProperty(x => x.PasswordHash, u.PasswordHash)
                        .SetProperty(x => x.MustChangePassword, u.MustChangePassword)
                        .SetProperty(x => x.RoleId, u.RoleId));
                }
            }
        }

        // ═══════════════════════════════════════════════
        // PaymentAllocationCycle — تسوية الدفعات العامة على الفواتير (منع التحصيل المزدوج):
        //  فاتورتان بلا سداد + دفعة عامة ⇒ تخصيص الأقدم أولًا ثم رفض أي سداد يتجاوز
        //  المتبقي. القديم كان يحسب «متبقي الفاتورة» من دفعاتها المرتبطة فقط فيسمح
        //  بتحصيل الدَّين نفسه مرتين (مرة عامة ومرة على الفاتورة).
        // ═══════════════════════════════════════════════
        private static async Task PaymentAllocationCycle()
        {
            Console.WriteLine("--- PAYMENT ALLOCATION (general vs invoice) ---");
            using var s = Scope();
            var db = Svc<Sayl.Core.Data.SaylDbContext>(s);
            var parties = Svc<clsPartyService>(s);
            var checkout = Svc<clsCheckoutService>(s);
            var purchase = Svc<clsPurchaseService>(s);
            var payments = Svc<clsPaymentService>(s);
            var sessions = Svc<clsPosSessionService>(s);
            var admin = await db.Users.FirstAsync(u => u.Username == "admin");

            var acting = await sessions.GetOpenSessionByUserAsync(admin.UserId);
            int sessionId = (acting.IsSuccess && acting.Data != null)
                ? acting.Data.SessionId
                : (await sessions.OpenSessionAsync(admin.UserId, 0m)).Data;
            DateOnly today = DateOnly.FromDateTime(DateTime.Today);

            string stamp = "PA-" + DateTime.Now.Ticks.ToString()[^8..];
            int customerId, supplierId;
            using (var partyScope = Scope())
            {
                var psvc = Svc<clsPartyService>(partyScope);
                var cust = await psvc.AddNewPartyWithRoleAsync(
                    new DtoParty { PartyType = 'C', Name = "عميل تسوية " + stamp }, actorUserId: admin.UserId);
                var sup = await psvc.AddNewPartyWithRoleAsync(
                    new DtoParty { PartyType = 'S', Name = "مورد تسوية " + stamp }, actorUserId: admin.UserId);
                if (!cust.IsSuccess || !sup.IsSuccess)
                { Check("allocation parties created", false, cust.Message + " / " + sup.Message); return; }
                customerId = cust.Data; supplierId = sup.Data;
            }

            // ── عميل: فاتورتان بلا أي سداد ──
            var saleA = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = admin.UserId, SessionId = sessionId, CustomerId = customerId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 10, UnitPrice = 10m } },
                Payments = new List<DtoPaymentInfo>()
            });
            var saleB = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = admin.UserId, SessionId = sessionId, CustomerId = customerId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 5, UnitPrice = 10m } },
                Payments = new List<DtoPaymentInfo>()
            });
            Check("allocation unpaid sales created", saleA.IsSuccess && saleB.IsSuccess,
                saleA.Message + " / " + saleB.Message);
            if (!saleA.IsSuccess || !saleB.IsSuccess) return;

            // دفعة عامة 120: تُخصَّص A(100) كاملة ثم B(20) — الأقدم أولًا
            var gen = await payments.ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            { PartyId = customerId, PartyType = 'C', Amount = 120m, PaymentDate = today, Method = "cash" });
            Check("general payment 120 accepted", gen.IsSuccess, gen.Message);

            var (statusA, balanceC) = await VerifyAsync(async vdb => (
                await vdb.Invoices.Where(i => i.InvoiceId == saleA.Data).Select(i => i.PaymentStatus).FirstAsync(),
                await vdb.Customers.Where(c => c.PartyId == customerId).Select(c => c.Balance ?? 0m).FirstAsync()));
            Check("oldest invoice fully covered by general payment", statusA == Common.Enums.enPaymentStatus.paid, $"status={statusA}");
            Check("party balance 150->30", balanceC == 30m, $"balance={balanceC}");

            // التحصيل المزدوج (الثغرة الأصلية): سداد على A المغطاة بدفعة عامة ⇒ مرفوض
            var dup = await payments.PayInvoiceAsync(new DtoPaymentRequest
            { InvoiceId = saleA.Data, Amount = 1m, PaymentDate = today, Method = "cash" });
            Check("double collection on covered invoice rejected", !dup.IsSuccess, dup.Message);

            // Even with a STALE unpaid status (بيانات قديمة قبل الإصلاح) التخصيص يمنع السداد
            await VerifyAsync(async vdb =>
            {
                await vdb.Invoices.Where(i => i.InvoiceId == saleA.Data)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.PaymentStatus, Common.Enums.enPaymentStatus.unpaid));
                return true;
            });
            var dupStale = await payments.PayInvoiceAsync(new DtoPaymentRequest
            { InvoiceId = saleA.Data, Amount = 10m, PaymentDate = today, Method = "cash" });
            Check("double collection blocked by allocation (stale status)", !dupStale.IsSuccess, dupStale.Message);

            // سداد فاتورة B: المتبقي 30 فقط — 40 يُرفض، و30 يقبَل ويغلق الفاتورة
            var overB = await payments.PayInvoiceAsync(new DtoPaymentRequest
            { InvoiceId = saleB.Data, Amount = 40m, PaymentDate = today, Method = "cash" });
            Check("invoice payment above allocated remaining rejected", !overB.IsSuccess, overB.Message);
            var payB = await payments.PayInvoiceAsync(new DtoPaymentRequest
            { InvoiceId = saleB.Data, Amount = 30m, PaymentDate = today, Method = "cash" });
            Check("exact remaining accepted", payB.IsSuccess, payB.Message);
            var (statusB, balanceC2) = await VerifyAsync(async vdb => (
                await vdb.Invoices.Where(i => i.InvoiceId == saleB.Data).Select(i => i.PaymentStatus).FirstAsync(),
                await vdb.Customers.Where(c => c.PartyId == customerId).Select(c => c.Balance ?? 0m).FirstAsync()));
            Check("invoice B paid via general+invoice mix", statusB == Common.Enums.enPaymentStatus.paid, $"status={statusB}");
            Check("party balance reaches 0", balanceC2 == 0m, $"balance={balanceC2}");

            // لا تحصيل من رصيد صفري (لا رصيد دائن وهمي)
            var noPhantom = await payments.ReceiveCustomerPaymentAsync(new DtoPaymentRequest
            { PartyId = customerId, PartyType = 'C', Amount = 10m, PaymentDate = today, Method = "cash" });
            Check("collection from zero balance rejected", !noPhantom.IsSuccess, noPhantom.Message);

            // ── مورد: شراء بدفعة جزئية ثم سداد عام ثم سداد فاتورة ──
            // نطاق مستقل لإنشاء الشراء: خدمة الشراء تحدّث رصيد المورد بـSQL خام
            // (زيادة ذرية) — ولو شاركنا السياق نفسه لقرأ FindAsync رصيدًا قديمًا
            // من الـtracker (في الواجهة كل عملية بنطاقها الخاص — نفس النمط هنا)
            int purchId;
            using (var ps = Scope())
            {
                var psvc = Svc<clsPurchaseService>(ps);
                var purch = await psvc.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
                {
                    SupplierId = supplierId, UserId = admin.UserId,
                    Items = new List<DtoPurchaseItem> { new() { ProductId = 1, UnitCost = 10m, Quantity = 6, BatchNumber = "PA-" + DateTime.Now.Ticks % 100000 } },
                    Payment = new DtoPaymentInfo { Amount = 20m, Method = "cash", PaymentDate = today }
                });
                Check("partial purchase created (debt 40)", purch.IsSuccess, purch.Message);
                purchId = purch.IsSuccess ? purch.Data : 0;
            }
            if (purchId <= 0) return;

            var paySup = await payments.PaySupplierAsync(new DtoPaymentRequest
            { PartyId = supplierId, PartyType = 'S', Amount = 25m, PaymentDate = today, Method = "cash" });
            Check("general supplier payment 25 accepted", paySup.IsSuccess, paySup.Message);

            var (pStatus, balanceS) = await VerifyAsync(async vdb => (
                await vdb.Invoices.Where(i => i.InvoiceId == purchId).Select(i => i.PaymentStatus).FirstAsync(),
                await vdb.Suppliers.Where(x => x.PartyId == supplierId).Select(x => x.Balance ?? 0m).FirstAsync()));
            Check("purchase partially covered by general payment", pStatus == Common.Enums.enPaymentStatus.partial, $"status={pStatus}");
            Check("supplier balance 40->15", balanceS == 15m, $"balance={balanceS}");

            var dupSup = await payments.PayInvoiceAsync(new DtoPaymentRequest
            { InvoiceId = purchId, Amount = 20m, PaymentDate = today, Method = "cash" });
            Check("double supplier payment rejected", !dupSup.IsSuccess, dupSup.Message);
            var paySupInv = await payments.PayInvoiceAsync(new DtoPaymentRequest
            { InvoiceId = purchId, Amount = 15m, PaymentDate = today, Method = "cash" });
            Check("exact supplier remaining accepted", paySupInv.IsSuccess, paySupInv.Message);
            int supStatus2 = await VerifyAsync(async vdb =>
                (int)await vdb.Invoices.Where(i => i.InvoiceId == purchId).Select(i => i.PaymentStatus).FirstAsync());
            Check("purchase invoice fully paid", supStatus2 == (int)Common.Enums.enPaymentStatus.paid, $"status={supStatus2}");

            // B3: إغلاق درج الدورة ليبقى الهارنس قابلًا للتكرار
            if (sessionId > 0)
                await sessions.CloseSessionAsync(sessionId, 0m, admin.UserId);
        }

        // ═══════════════════════════════════════════════
        // RefundPlanCycle — أولوية الدَّين في الاسترداد + المرتجعات المتعددة:
        //  فاتورة 100 (مدفوع 50، دين 50) + مرتجع بضاعة 80
        //  ⇒ تخفيض الدين 50 (إلى صفر) واسترداد نقد 30.
        //  القديم كان يرد المدفوع أولًا فيترك دينًا خاطئًا على بضاعة رُجعت كلها.
        // ═══════════════════════════════════════════════
        private static async Task RefundPlanCycle()
        {
            Console.WriteLine("--- REFUND PLAN (debt-first + multiple returns) ---");
            using var s = Scope();
            var db = Svc<Sayl.Core.Data.SaylDbContext>(s);
            var parties = Svc<clsPartyService>(s);
            var checkout = Svc<clsCheckoutService>(s);
            var purchase = Svc<clsPurchaseService>(s);
            var returns = Svc<clsReturnService>(s);
            var sessions = Svc<clsPosSessionService>(s);
            var admin = await db.Users.FirstAsync(u => u.Username == "admin");

            var acting = await sessions.GetOpenSessionByUserAsync(admin.UserId);
            int sessionId = (acting.IsSuccess && acting.Data != null)
                ? acting.Data.SessionId
                : (await sessions.OpenSessionAsync(admin.UserId, 0m)).Data;

            string stamp = "RP-" + DateTime.Now.Ticks.ToString()[^8..];
            int customerId, supplierId;
            using (var partyScope = Scope())
            {
                var psvc = Svc<clsPartyService>(partyScope);
                var cust = await psvc.AddNewPartyWithRoleAsync(
                    new DtoParty { PartyType = 'C', Name = "عميل مرتجعات " + stamp }, actorUserId: admin.UserId);
                var sup = await psvc.AddNewPartyWithRoleAsync(
                    new DtoParty { PartyType = 'S', Name = "مورد مرتجعات " + stamp }, actorUserId: admin.UserId);
                if (!cust.IsSuccess || !sup.IsSuccess)
                { Check("refund parties created", false, cust.Message + " / " + sup.Message); return; }
                customerId = cust.Data; supplierId = sup.Data;
            }

            // بيع 100 (20×5) مدفوع نصفه نقدًا → دين 50
            var sale = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = admin.UserId, SessionId = sessionId, CustomerId = customerId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 20, UnitPrice = 5m } },
                Payments = new List<DtoPaymentInfo> { new() { Amount = 50m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
            });
            Check("refund probe sale (100, paid 50) created", sale.IsSuccess, sale.Message);
            if (!sale.IsSuccess) return;

            // المرتجع الأول: 16 وحدة = 80 → دين يُخفض 50 + نقد 30
            var ret1 = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
            {
                OriginalInvoiceId = sale.Data, UserId = admin.UserId, SessionId = sessionId,
                Items = new List<DtoReturnItem> { new() { ProductId = 1, Quantity = 16 } }
            });
            Check("return #1 (80) executed", ret1.IsSuccess, ret1.Message);
            if (ret1.IsSuccess)
            {
                var (retNet, cashOut, balanceAfter) = await VerifyAsync(async vdb => (
                    await vdb.Invoices.Where(i => i.InvoiceId == ret1.Data).Select(i => i.NetAmount).FirstAsync(),
                    await vdb.Payments.Where(p => p.InvoiceId == ret1.Data && p.Type == Common.Enums.enPaymentType.outcome)
                        .SumAsync(p => (decimal?)p.Amount) ?? 0m,
                    await vdb.Customers.Where(c => c.PartyId == customerId).Select(c => c.Balance ?? 0m).FirstAsync()));
                Check("return #1 net -80.00", retNet == -80m, $"net={retNet}");
                Check("return #1 cash refund 30 (debt takes 50 first)", cashOut == 30m, $"cash={cashOut}");
                Check("return #1 debt cleared 50->0", balanceAfter == 0m, $"balance={balanceAfter}");
            }

            // المرتجع الثاني: 4 وحدات = 20 → الدين صفر أصلًا ⇒ نقد 20 (ضمن المدفوع المتبقي)
            var ret2 = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
            {
                OriginalInvoiceId = sale.Data, UserId = admin.UserId, SessionId = sessionId,
                Items = new List<DtoReturnItem> { new() { ProductId = 1, Quantity = 4 } }
            });
            Check("return #2 (20) executed", ret2.IsSuccess, ret2.Message);
            if (ret2.IsSuccess)
            {
                var (cashOut2, balanceAfter2) = await VerifyAsync(async vdb => (
                    await vdb.Payments.Where(p => p.InvoiceId == ret2.Data && p.Type == Common.Enums.enPaymentType.outcome)
                        .SumAsync(p => (decimal?)p.Amount) ?? 0m,
                    await vdb.Customers.Where(c => c.PartyId == customerId).Select(c => c.Balance ?? 0m).FirstAsync()));
                Check("return #2 cash refund 20 (no debt left)", cashOut2 == 20m, $"cash={cashOut2}");
                Check("return #2 balance stays 0", balanceAfter2 == 0m, $"balance={balanceAfter2}");
            }

            // كل الكمية رُجعت — أي مرتجع إضافي مرفوض
            var ret3 = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
            {
                OriginalInvoiceId = sale.Data, UserId = admin.UserId, SessionId = sessionId,
                Items = new List<DtoReturnItem> { new() { ProductId = 1, Quantity = 1 } }
            });
            Check("return beyond quantity rejected", !ret3.IsSuccess, ret3.Message);

            // ── مورد: شراء 100 مدفوع 50 + مرتجعان (نفس معادلة العميل) ──
            // نطاق مستقل للشراء (SQL خام لرصيد المورد — نفس سبب دورة التسوية أعلاه)
            int purchId;
            using (var ps2 = Scope())
            {
                var psvc2 = Svc<clsPurchaseService>(ps2);
                var purch = await psvc2.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
                {
                    SupplierId = supplierId, UserId = admin.UserId,
                    Items = new List<DtoPurchaseItem> { new() { ProductId = 1, UnitCost = 5m, Quantity = 20, BatchNumber = "RP-" + DateTime.Now.Ticks % 100000 } },
                    Payment = new DtoPaymentInfo { Amount = 50m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) }
                });
                Check("refund probe purchase (100, paid 50) created", purch.IsSuccess, purch.Message);
                purchId = purch.IsSuccess ? purch.Data : 0;
            }
            if (purchId > 0)
            {
                var sret1 = await returns.CreateSupplierReturnAsync(new DtoReturnRequest
                {
                    OriginalInvoiceId = purchId, UserId = admin.UserId, SessionId = sessionId,
                    Items = new List<DtoReturnItem> { new() { ProductId = 1, Quantity = 16 } }
                });
                Check("supplier return #1 (80) executed", sret1.IsSuccess, sret1.Message);
                if (sret1.IsSuccess)
                {
                    var (cashIn, balanceS) = await VerifyAsync(async vdb => (
                        await vdb.Payments.Where(p => p.InvoiceId == sret1.Data && p.Type == Common.Enums.enPaymentType.income)
                            .SumAsync(p => (decimal?)p.Amount) ?? 0m,
                        await vdb.Suppliers.Where(x => x.PartyId == supplierId).Select(x => x.Balance ?? 0m).FirstAsync()));
                    Check("supplier return #1 cash-in 30 (debt takes 50 first)", cashIn == 30m, $"cash={cashIn}");
                    Check("supplier return #1 debt cleared 50->0", balanceS == 0m, $"balance={balanceS}");
                }

                var sret2 = await returns.CreateSupplierReturnAsync(new DtoReturnRequest
                {
                    OriginalInvoiceId = purchId, UserId = admin.UserId, SessionId = sessionId,
                    Items = new List<DtoReturnItem> { new() { ProductId = 1, Quantity = 4 } }
                });
                Check("supplier return #2 (20) executed", sret2.IsSuccess, sret2.Message);
                if (sret2.IsSuccess)
                {
                    var (cashIn2, balanceS2) = await VerifyAsync(async vdb => (
                        await vdb.Payments.Where(p => p.InvoiceId == sret2.Data && p.Type == Common.Enums.enPaymentType.income)
                            .SumAsync(p => (decimal?)p.Amount) ?? 0m,
                        await vdb.Suppliers.Where(x => x.PartyId == supplierId).Select(x => x.Balance ?? 0m).FirstAsync()));
                    Check("supplier return #2 cash-in 20", cashIn2 == 20m, $"cash={cashIn2}");
                    Check("supplier return #2 balance stays 0", balanceS2 == 0m, $"balance={balanceS2}");
                }
            }

            // B3: إغلاق درج الدورة ليبقى الهارنس قابلًا للتكرار
            if (sessionId > 0)
                await sessions.CloseSessionAsync(sessionId, 0m, admin.UserId);
        }

        // ───────────────── 2. Session + direct sale ─────────────────
        private static int _saleInvoiceId;

        // ═══════════════════════════════════════════════
        // QuickAddPartyCycle — مسار «إضافة عميل جديد» من شاشة الكاشير:
        //  1) إضافتان متتاليتان بلا بريد (منPosQuickAdd=true + كاشير 0 صلاحيات)
        //     — قبل Upgrade_10 كان الثاني يرتد بـ2601 (فهرس فريد يقبض بعد NULL واحد).
        //  2) بريد مكرر لموجود فعلًا — يجب أن يفشل برسالة صريحة فيها «مستخدم مسبقًا».
        //  3) تلطيف النظام: صفوف الاختبار تُعطَّل (حذف ناعم) لا تُمس بيانات حقيقية.
        // ═══════════════════════════════════════════════
        private static async Task QuickAddPartyCycle()
        {
            Console.WriteLine("--- POS QUICK ADD PARTY ---");
            using var s = Scope();
            var db = Svc<Sayl.Core.Data.SaylDbContext>(s);
            var parties = Svc<clsPartyService>(s);

            var cashier = await db.Users.FirstAsync(u => u.Username == "cashier1");
            string stamp = "QA-" + DateTime.Now.Ticks.ToString()[^8..];

            // 1) إضافتان بلا بريد — كلتاهما يجب أن تنجحا
            var r1 = await parties.AddNewPartyWithRoleAsync(
                new DtoParty { PartyType = 'C', Name = "زبون سريع " + stamp + " A", Phone1 = "079000001" },
                actorUserId: cashier.UserId, fromPosQuickAdd: true);
            Check("quick-add #1 without email succeeds", r1.IsSuccess, r1.Message);

            var r2 = await parties.AddNewPartyWithRoleAsync(
                new DtoParty { PartyType = 'C', Name = "زبون سريع " + stamp + " B", Phone1 = "079000002" },
                actorUserId: cashier.UserId, fromPosQuickAdd: true);
            Check("quick-add #2 without email succeeds (filtered unique index)", r2.IsSuccess, r2.Message);

            // نقطتان محجوزتان لخطوة 2 قيد الاختبار
            List<int> created = new();
            if (r1.IsSuccess) created.Add(r1.Data);
            if (r2.IsSuccess) created.Add(r2.Data);

            // 2) بريد مكرر: نستعير بريد طرف قائم وولها للزبون الجديد
            var withEmail = await db.Parties
                .Where(p => p.Email != null)
                .Select(p => new { p.PartyId, p.Email })
                .FirstOrDefaultAsync();
            if (withEmail != null && r2.IsSuccess)
            {
                var dup = await parties.AddNewPartyWithRoleAsync(
                    new DtoParty { PartyType = 'C', Name = "زبون بريد مكرر " + stamp, Email = withEmail.Email },
                    actorUserId: cashier.UserId, fromPosQuickAdd: true);
                Check("duplicate email rejected with clear message",
                    !dup.IsSuccess && (dup.Message?.Contains("مستخدم مسبقًا") ?? false),
                    dup.Message ?? "");
            }
            else
                Check("duplicate email test skipped (no seeded email or quick-add#2 failed)", false, withEmail?.Email ?? "");

            // 3) تحقق مستقل: بانين أفادان بلا بريد يُقبّل كلاهما في القاعدة (NULL كثرة مسموحة)
            int nullEmailCount = await VerifyAsync(async vdb => await vdb.Parties
                .Where(p => p.PartyType == 'C')
                .CountAsync(p => p.Email == null));
            Check("database now holds multiple NULL-email parties", nullEmailCount >= 2, $"NULL count={nullEmailCount}");

            // 4) تهذيف: تعطيل صفوف الاختبار فقط (حذف ناعم — بلا استيفاء بيانات حقيقية)
            await VerifyAsync(async vdb =>
            {
                foreach (var id in created)
                {
                    var p = await vdb.Parties.FindAsync(id);
                    if (p != null) vdb.Entry(p).Property(x => x.IsActive).CurrentValue = false;
                }
                await vdb.SaveChangesAsync();
                return true;
            });
        }

        private static async Task<(int sessionId, int cashierId)> SessionAndSaleCycle()
        {
            Console.WriteLine("--- SESSION + SALE ---");
            using var s = Scope();
            var sessions = Svc<clsPosSessionService>(s);
            var checkout = Svc<clsCheckoutService>(s);
            var products = Svc<clsProductService>(s);

            var admin = await s.ServiceProvider.GetRequiredService<Sayl.Core.Data.SaylDbContext>().Users.FirstAsync(u => u.Username == "admin");
            var cashier = await s.ServiceProvider.GetRequiredService<Sayl.Core.Data.SaylDbContext>().Users.FirstAsync(u => u.Username == "cashier1");

            // close any leftover open session from previous runs
            var existing = await sessions.GetOpenSessionByUserAsync(cashier.UserId);
            if (existing.IsSuccess && existing.Data != null)
                await sessions.CloseSessionAsync(existing.Data.SessionId, 0, cashier.UserId);

            var open = await sessions.OpenSessionAsync(cashier.UserId, 100m);
            Check("open session (opening=100)", open.IsSuccess && open.Data > 0);
            int sessionId = open.IsSuccess ? open.Data : 0;

            // barcode scan path
            var byBarcode = await products.GetProductByBarcodeAsync("6281002123450");
            Check("barcode lookup", byBarcode.IsSuccess && byBarcode.Data != null, $"found={byBarcode.Data?.Name}");

            // snapshot BEFORE (isolated context — no shared tracking)
            int prodId = byBarcode.Data!.ProductId;
            decimal invBeforeVal;
            List<(int BatchId, int Qty)> batchesBefore;
            using (var vs = Scope())
            {
                var vdb = Svc<Sayl.Core.Data.SaylDbContext>(vs);
                invBeforeVal = await vdb.Inventories.AsNoTracking().Where(i => i.ProductId == prodId).Select(i => i.QuantityAvailable ?? 0).FirstAsync();
                batchesBefore = await vdb.Batches.AsNoTracking()
                    .Where(b => b.ProductId == prodId && b.BatchQuantity > 0)
                    .OrderBy(b => b.ExpiryDate ?? DateOnly.MaxValue).ThenBy(b => b.CreatedAt)
                    .Select(b => new ValueTuple<int, int>(b.BatchId, b.BatchQuantity))
                    .ToListAsync();
            }

            decimal netExpected = byBarcode.Data.BasePrice * 2 * 1.10m; // 10% tax
            var request = new DtoCheckoutRequest
            {
                UserId = cashier.UserId,
                SessionId = sessionId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = prodId, Quantity = 2 } },
                TaxPercent = 10,
                Payments = new List<DtoPaymentInfo>
                {
                    new() { Amount = Math.Round(netExpected / 2, 2), Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) },
                    new() { Amount = Math.Round(netExpected - Math.Round(netExpected / 2, 2), 2), Method = "card", PaymentDate = DateOnly.FromDateTime(DateTime.Today) }
                }
            };

            var sale = await checkout.ProcessCheckoutAsync(request);
            Check("direct sale with split payments", sale.IsSuccess, sale.Message);
            if (sale.IsSuccess) _saleInvoiceId = sale.Data;

            // verify AFTER via fresh context
            using (var vs = Scope())
            {
                var vdb = Svc<Sayl.Core.Data.SaylDbContext>(vs);
                var invAfter = await vdb.Inventories.AsNoTracking().Where(i => i.ProductId == prodId).Select(i => i.QuantityAvailable ?? 0).FirstAsync();
                Check("stock deducted", invAfter == invBeforeVal - 2, $"{invBeforeVal}->{invAfter}");
                if (batchesBefore.Count > 0)
                {
                    var firstId = batchesBefore[0].BatchId;
                    var qtyAfter = await vdb.Batches.AsNoTracking().Where(b => b.BatchId == firstId).Select(b => b.BatchQuantity).FirstAsync();
                    Check("FEFO batch consumed", qtyAfter == batchesBefore[0].Qty - 2,
                        $"batch {firstId}: {batchesBefore[0].Qty}->{qtyAfter}");

                    // VULN-36: بند فاتورة البيع يحمل دفعة FEFO الفعلية (كانت null دائمًا)
                    var itemBatch = await vdb.InvoiceItems.AsNoTracking()
                        .Where(ii => ii.InvoiceId == _saleInvoiceId && ii.ProductId == prodId)
                        .Select(ii => ii.BatchId).FirstOrDefaultAsync();
                    Check("sale item carries FEFO batch", itemBatch == firstId,
                        $"itemBatchId={itemBatch} expectedFefo={firstId}");
                }
                var payCount = await vdb.Payments.CountAsync(p => p.InvoiceId == _saleInvoiceId);
                Check("two payment rows saved", payCount == 2, $"count={payCount}");
            }

            // insufficient stock guard
            var huge = new DtoCheckoutRequest
            {
                UserId = cashier.UserId,
                SessionId = sessionId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = prodId, Quantity = 999999 } },
                Payments = new List<DtoPaymentInfo> { new() { Amount = 999999, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
            };
            var oversell = await checkout.ProcessCheckoutAsync(huge);
            Check("oversell rejected with clear message", !oversell.IsSuccess && !oversell.Message.Contains("فشلت عملية البيع"),
                $"msg='{oversell.Message}'");

            // VULN-14: snapshot سعر السلة — الفاتورة تُبنى على سعر لحظة الإضافة
            // حتى لو تغير سعر المنتج قبل الحفظ (كان الزبون يُفترس بصمت)
            {
                using var ps = Scope();
                var pdb = Svc<Sayl.Core.Data.SaylDbContext>(ps);
                var prod = await pdb.Products.FindAsync(prodId);
                decimal original = prod.BasePrice;
                prod.BasePrice = original + 100m; // محاكاة تعديل سعر أثناء بناء سلة
                await pdb.SaveChangesAsync();
                prod.BasePrice = original; // إرجاع فوري — الفحص على الطلب فقط
                var snapReq = new DtoCheckoutRequest
                {
                    UserId = cashier.UserId,
                    SessionId = sessionId,
                    Items = new List<DtoCheckoutItem> { new() { ProductId = prodId, Quantity = 1, UnitPrice = original } },
                    Payments = new List<DtoPaymentInfo> { new() { Amount = original, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
                };
                var snapSale = await checkout.ProcessCheckoutAsync(snapReq);
                Check("VULN-14 snapshot sale executes", snapSale.IsSuccess, snapSale.Message);
                if (snapSale.IsSuccess)
                {
                    var pricedAt = await VerifyAsync(vdb =>
                        vdb.InvoiceItems.AsNoTracking()
                            .Where(ii => ii.InvoiceId == snapSale.Data && ii.ProductId == prodId)
                            .Select(ii => ii.ItemPrice).FirstAsync());
                    Check("VULN-14 invoice priced at cart snapshot", pricedAt == original,
                        $"priced={pricedAt} expected={original}");
                }
                // إرجاع سعر المنتج في القاعدة (المحاكاة انتهت)
                await pdb.SaveChangesAsync();
                var restored = await VerifyAsync(vdb =>
                    vdb.Products.AsNoTracking().Where(p => p.ProductId == prodId).Select(p => p.BasePrice).FirstAsync());
                Check("product price restored", restored == original, $"{restored}");
            }

            return (sessionId, cashier.UserId);
        }

        // ───────────────── 3. Suspend / resume / cancel ─────────────────
        private static int _suspendedId;

        private static async Task SuspendResumeCancelCycle(int sessionId, int cashierId)
        {
            Console.WriteLine("--- SUSPEND/RESUME/CANCEL ---");
            using var s = Scope();
            var checkout = Svc<clsCheckoutService>(s);
            int prodId = 1;

            decimal inv0Avail, inv0Resv;
            using (var vs = Scope())
            {
                var vdb = Svc<Sayl.Core.Data.SaylDbContext>(vs);
                var r0 = await vdb.Inventories.AsNoTracking().Where(i => i.ProductId == prodId)
                    .Select(i => new { i.QuantityAvailable, i.QuantityReserved }).FirstAsync();
                inv0Avail = r0.QuantityAvailable ?? 0;
                inv0Resv = r0.QuantityReserved ?? 0;
            }

            // suspend → reserve
            var susp = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = cashierId,
                SessionId = sessionId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = prodId, Quantity = 3 } }
            }, suspend: true);
            Check("suspend reserves stock", susp.IsSuccess, susp.Message);
            _suspendedId = susp.Data;

            var reservedNow = await VerifyAsync(db =>
                db.Inventories.AsNoTracking().Where(i => i.ProductId == prodId)
                    .Select(i => i.QuantityReserved ?? 0).FirstAsync());
            Check("reserved incremented", reservedNow == inv0Resv + 3, $"{inv0Resv}->{reservedNow}");

            // resume with split payments
            var net = await VerifyAsync(db =>
                db.Invoices.Where(i => i.InvoiceId == _suspendedId).Select(i => i.NetAmount).FirstAsync());
            var done = await checkout.CompleteSuspendedInvoiceAsync(_suspendedId, new List<DtoPaymentInfo>
            {
                new() { Amount = net / 2, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) },
                new() { Amount = net - net / 2, Method = "bank_transfer", PaymentDate = DateOnly.FromDateTime(DateTime.Today) }
            }, actorUserId: cashierId);
            Check("resume completes with split payments", done.IsSuccess, done.Message);

            var resumed = await VerifyAsync(db =>
                db.Inventories.AsNoTracking().Where(i => i.ProductId == prodId)
                    .Select(i => new { A = i.QuantityAvailable ?? 0, R = i.QuantityReserved ?? 0 }).FirstAsync());
            Check("reservation released + stock deducted",
                resumed.R == inv0Resv && resumed.A == inv0Avail - 3,
                $"avail={resumed.A} resv={resumed.R}");

            // suspend again then cancel
            var susp2 = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = cashierId,
                SessionId = sessionId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = prodId, Quantity = 1 } }
            }, suspend: true);
            Check("second suspend", susp2.IsSuccess, susp2.Message);

            var cancelWrongUser = await checkout.CancelSuspendedInvoiceAsync(susp2.Data, 999999);
            Check("cancel by unknown actor rejected", !cancelWrongUser.IsSuccess);

            var cancelled = await checkout.CancelSuspendedInvoiceAsync(susp2.Data, cashierId);
            Check("own-cancel allowed", cancelled.IsSuccess, cancelled.Message);

            var finalResv = await VerifyAsync(db =>
                db.Inventories.AsNoTracking().Where(i => i.ProductId == prodId)
                    .Select(i => i.QuantityReserved ?? 0).FirstAsync());
            Check("cancel released reservation", finalResv == inv0Resv, $"resv={finalResv}");
        }

        // ───────────────── 3b. Reservation ownership (suspended invoices) ─────────────────
        private static async Task ReservationOwnershipCycle()
        {
            Console.WriteLine("--- RESERVATION OWNERSHIP ---");
            int cashierId = await VerifyAsync(db =>
                db.Users.Where(u => u.Username == "cashier1").Select(u => u.UserId).FirstAsync());

            // منتج معزول بمخزون 10 بالضبط لكل جولة — barcode/اسم فريدان بالوقت ⇒ التكرار آمن
            string tag = "RESV" + DateTime.Now.Ticks;
            int prodId = 0;
            using (var ps = Scope())
            {
                var pdb = Svc<Sayl.Core.Data.SaylDbContext>(ps);
                var prod = new Product { Name = tag, Barcode = tag, Type = "stock", BasePrice = 10m, CostPrice = 5m, IsActive = true };
                pdb.Products.Add(prod);
                await pdb.SaveChangesAsync();
                pdb.Inventories.Add(new Inventory { ProductId = prod.ProductId, QuantityAvailable = 10, QuantityReserved = 0 });
                pdb.Batches.Add(new Batch { ProductId = prod.ProductId, BatchNumber = tag, BatchQuantity = 10 });
                await pdb.SaveChangesAsync();
                prodId = prod.ProductId;
            }

            async Task<(int avail, int reserved, int rowCount, int rowsSum)> Snap()
                => await VerifyAsync(async db =>
                {
                    var inv = await db.Inventories.AsNoTracking().Where(i => i.ProductId == prodId)
                        .Select(i => new { A = i.QuantityAvailable ?? 0, R = i.QuantityReserved ?? 0 }).FirstAsync();
                    var rows = await db.StockReservations.AsNoTracking().Where(r => r.ProductId == prodId)
                        .Select(r => r.Quantity).ToListAsync();
                    return (inv.A, inv.R, rows.Count, rows.Sum());
                });

            // كل عملية بـscope جديد — مطابق لاستدعاء الواجهة الفعلي (PosForm/SuspendedInvoicesForm)
            async Task<(bool ok, string msg, int invoiceId)> SuspendMsg(int qty)
            {
                using var os = Scope();
                var r = await Svc<clsCheckoutService>(os).ProcessCheckoutAsync(new DtoCheckoutRequest
                {
                    UserId = cashierId,
                    Items = new List<DtoCheckoutItem> { new() { ProductId = prodId, Quantity = qty } }
                }, suspend: true);
                return (r.IsSuccess, r.Message, r.IsSuccess ? r.Data : 0);
            }

            async Task Provision(int qty)
            {
                using var ps = Scope();
                var pdb = Svc<Sayl.Core.Data.SaylDbContext>(ps);
                pdb.Batches.Add(new Batch { ProductId = prodId, BatchNumber = tag + "-" + DateTime.Now.Ticks, BatchQuantity = qty });
                await pdb.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE Inventory SET QuantityAvailable = QuantityAvailable + {qty} WHERE ProductId = {prodId}");
                await pdb.SaveChangesAsync();
            }

            async Task<bool> Complete(int invoiceId)
            {
                if (invoiceId <= 0) return false; // فشل التعليق سلفًا — لا يُهرَس الهارنس
                decimal net = await VerifyAsync(db =>
                    db.Invoices.Where(i => i.InvoiceId == invoiceId).Select(i => i.NetAmount).FirstAsync());
                using var os = Scope();
                var r = await Svc<clsCheckoutService>(os).CompleteSuspendedInvoiceAsync(invoiceId, new List<DtoPaymentInfo>
                {
                    new() { Amount = net, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) }
                }, actorUserId: cashierId);
                return r.IsSuccess;
            }

            // ══ 1) التعليق على كامل المخزون ثم الإكمال — كان يفشل قبل الإصلاح ══
            var (okA, msgA, invA) = await SuspendMsg(10);
            Check("suspend A reserves full stock (10)", okA, msgA);

            var (okOver, msgOver, _) = await SuspendMsg(1);
            Check("other invoice cannot reserve beyond reserved stock", !okOver, msgOver);

            Check("complete A consumes its own reservation", await Complete(invA));
            var snap1 = await Snap();
            Check("after A completed: avail=0 reserved=0 rows=0",
                snap1.avail == 0 && snap1.reserved == 0 && snap1.rowCount == 0,
                $"avail={snap1.avail} resv={snap1.reserved} rows={snap1.rowCount}");

            // ══ 2) عزل الحجوزات: A(3) و B(4) — إكمال A لا يمس حجز B ══
            await Provision(10);
            var pre2 = await Snap();
            Check("provision 10 before step 2", pre2.avail == 10 && pre2.reserved == 0, $"avail={pre2.avail} resv={pre2.reserved}");
            var (okA2, msgA2, invA2) = await SuspendMsg(3);
            var (okB2, msgB2, invB2) = await SuspendMsg(4);
            Check("suspend A2(3) and B2(4)", okA2 && okB2, $"A2: {msgA2} | B2: {msgB2}");
            var snap2 = await Snap();
            Check("aggregate matches invoice rows (7)", snap2.reserved == 7 && snap2.rowsSum == 7 && snap2.rowCount == 2,
                $"resv={snap2.reserved} rowsSum={snap2.rowsSum} rows={snap2.rowCount}");

            Check("complete A2 succeeds without touching B2", await Complete(invA2));
            var snap3 = await Snap();
            Check("B2 reservation intact after A2 completed (reserved=4)",
                snap3.reserved == 4 && snap3.rowsSum == 4 && snap3.rowCount == 1 && snap3.avail == 7,
                $"avail={snap3.avail} resv={snap3.reserved} rowsSum={snap3.rowsSum} rows={snap3.rowCount}");

            // ══ 3) بيع مباشر أثناء حجز B: المتاح الصافي 7−4=3 ══
            async Task<(bool ok, string msg)> DirectSale(int qty)
            {
                using var os = Scope();
                var r = await Svc<clsCheckoutService>(os).ProcessCheckoutAsync(new DtoCheckoutRequest
                {
                    UserId = cashierId,
                    Items = new List<DtoCheckoutItem> { new() { ProductId = prodId, Quantity = qty } },
                    Payments = new List<DtoPaymentInfo> { new() { Amount = qty * 10m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
                });
                return (r.IsSuccess, r.Message);
            }

            var (d1ok, d1msg) = await DirectSale(3);
            Check("direct sale allowed up to net available (3)", d1ok, d1msg);
            var snap4 = await Snap();
            Check("B2 reservation untouched by direct sale (reserved=4)",
                snap4.reserved == 4 && snap4.rowsSum == 4 && snap4.rowCount == 1 && snap4.avail == 4,
                $"avail={snap4.avail} resv={snap4.reserved}");

            var (d2ok, d2msg) = await DirectSale(1);
            Check("direct sale cannot consume B2 reserved stock", !d2ok, d2msg);
            var snap5 = await Snap();
            Check("B2 reservation intact after rejected direct sale",
                snap5.reserved == 4 && snap5.rowsSum == 4 && snap5.avail == 4,
                $"avail={snap5.avail} resv={snap5.reserved}");

            // ══ 4) إلغاء B2 يحرر حجزها فقط — والإلغاء المكرر يُرفض ══
            async Task<(bool ok, string msg)> CancelInv(int invoiceId)
            {
                using var os = Scope();
                var r = await Svc<clsCheckoutService>(os).CancelSuspendedInvoiceAsync(invoiceId, cashierId);
                return (r.IsSuccess, r.Message);
            }

            var (cok, cmsg) = await CancelInv(invB2);
            Check("cancel B2 releases only its own reservation", cok, cmsg);
            var snap6 = await Snap();
            Check("after B2 cancelled: reserved=0 rows=0 avail=4",
                snap6.reserved == 0 && snap6.rowCount == 0 && snap6.avail == 4,
                $"avail={snap6.avail} resv={snap6.reserved} rows={snap6.rowCount}");

            var (ctok, ctmsg) = await CancelInv(invB2);
            Check("duplicate cancel rejected", !ctok, ctmsg);

            // ══ 5) إكمال مزدوج متوازٍ: فائز واحد بالضبط وخصم مرة واحدة ══
            var (okC, msgC, invC) = await SuspendMsg(2);
            Check("suspend C(2)", okC, msgC);
            decimal netC = await VerifyAsync(db =>
                db.Invoices.Where(i => i.InvoiceId == invC).Select(i => i.NetAmount).FirstAsync());
            var pays = new List<DtoPaymentInfo>
            {
                new() { Amount = netC, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) }
            };
            var results = await Task.WhenAll(
                Task.Run(async () => { using var s1 = Scope(); return await Svc<clsCheckoutService>(s1).CompleteSuspendedInvoiceAsync(invC, pays, cashierId); }),
                Task.Run(async () => { using var s2 = Scope(); return await Svc<clsCheckoutService>(s2).CompleteSuspendedInvoiceAsync(invC, pays, cashierId); }));
            var (r1, r2) = (results[0], results[1]);
            Check("concurrent completion: exactly one wins", (r1.IsSuccess ? 1 : 0) + (r2.IsSuccess ? 1 : 0) == 1,
                $"r1={r1.IsSuccess} r2={r2.IsSuccess}");
            var snap7 = await Snap();
            Check("concurrent completion deducted once, rows cleared",
                snap7.avail == 2 && snap7.reserved == 0 && snap7.rowCount == 0,
                $"avail={snap7.avail} resv={snap7.reserved} rows={snap7.rowCount}");
        }

        // ───────────────── 4. Return ─────────────────
        private static async Task ReturnCycle()
        {
            Console.WriteLine("--- RETURN ---");
            using var s = Scope();
            var returns = Svc<clsReturnService>(s);
            var cashierId = await VerifyAsync(db =>
                db.Users.Where(u => u.Username == "cashier1").Select(u => u.UserId).FirstAsync());

            if (_saleInvoiceId <= 0) { Check("return skipped (no sale)", false); return; }

            var item = await VerifyAsync(db =>
                db.InvoiceItems.AsNoTracking().Where(i => i.InvoiceId == _saleInvoiceId)
                    .Select(i => new { i.ProductId, i.BatchId }).FirstAsync());

            int availBefore = await VerifyAsync(db =>
                db.Inventories.AsNoTracking().Where(i => i.ProductId == item.ProductId)
                    .Select(i => i.QuantityAvailable ?? 0).FirstAsync());
            int? batchBefore = item.BatchId.HasValue
                ? await VerifyAsync(db => db.Batches.AsNoTracking()
                    .Where(b => b.BatchId == item.BatchId).Select(b => (int?)b.BatchQuantity).FirstAsync())
                : null;

            var ret = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
            {
                OriginalInvoiceId = _saleInvoiceId,
                UserId = cashierId,
                Items = new List<DtoReturnItem> { new() { ProductId = item.ProductId, Quantity = 1 } }
            });
            Check("customer return executed", ret.IsSuccess, ret.Message);

            int availAfter = await VerifyAsync(db =>
                db.Inventories.AsNoTracking().Where(i => i.ProductId == item.ProductId)
                    .Select(i => i.QuantityAvailable ?? 0).FirstAsync());
            Check("stock restored", availAfter == availBefore + 1, $"{availBefore}->{availAfter}");

            if (item.BatchId.HasValue && batchBefore.HasValue)
            {
                int batchAfter = await VerifyAsync(db => db.Batches.AsNoTracking()
                    .Where(b => b.BatchId == item.BatchId).Select(b => b.BatchQuantity).FirstAsync());
                Check("batch quantity restored", batchAfter == batchBefore + 1);
            }

            // refund payment recorded as outcome
            var refundPaid = await VerifyAsync(db =>
                db.Payments.AnyAsync(p => p.InvoiceId == ret.Data && p.Type == Common.Enums.enPaymentType.outcome));
            Check("cash refund recorded", refundPaid);

            // over-return guard
            var over = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
            {
                OriginalInvoiceId = _saleInvoiceId,
                UserId = cashierId,
                Items = new List<DtoReturnItem> { new() { ProductId = item.ProductId, Quantity = 99 } }
            });
            Check("over-return rejected", !over.IsSuccess, over.Message);
        }

        // ───────────────── 5. Cash desk ─────────────────
        private static async Task CashDeskCycle(int sessionId)
        {
            Console.WriteLine("--- CASH DESK + CLOSE ---");
            using var s = Scope();
            var cash = Svc<clsCashService>(s);
            var sessions = Svc<clsPosSessionService>(s);
            var db = Svc<Sayl.Core.Data.SaylDbContext>(s);
            var cashier = await db.Users.FirstAsync(u => u.Username == "cashier1");

            var inM = await cash.AddMovementAsync(sessionId, 'I', 50m, "تعبئة الدرج", cashier.UserId);
            var outM = await cash.AddMovementAsync(sessionId, 'O', 20m, "دفع للمندوب", cashier.UserId);
            Check("cash in/out recorded", inM.IsSuccess && outM.IsSuccess);

            var badType = await cash.AddMovementAsync(sessionId, 'X', 10m, "خطأ", cashier.UserId);
            Check("invalid movement type rejected", !badType.IsSuccess);

            var zero = await cash.AddMovementAsync(sessionId, 'I', 0m, "صفر", cashier.UserId);
            Check("zero amount rejected", !zero.IsSuccess);

            var summary = await cash.GetSessionCashSummaryAsync(sessionId);
            Check("summary loads", summary.IsSuccess && summary.Data != null);
            if (summary.Data != null)
            {
                decimal expected = summary.Data.OpeningAmount + summary.Data.CashSales - summary.Data.CashReturns
                                 + summary.Data.CashIn - summary.Data.CashOut;
                Check("expected-cash math consistent", summary.Data.ExpectedCash == expected,
                    $"{summary.Data.ExpectedCash} vs {expected}");
                Console.WriteLine($"   summary: opening={summary.Data.OpeningAmount} sales={summary.Data.CashSales} " +
                    $"ret={summary.Data.CashReturns} in={summary.Data.CashIn} out={summary.Data.CashOut} expected={summary.Data.ExpectedCash}");

                var closed = await sessions.CloseSessionAsync(sessionId, summary.Data.ExpectedCash, cashier.UserId);
                Check("close session Z", closed.IsSuccess, closed.Message);

                var stored = await db.PosSessions.AsNoTracking().FirstAsync(p => p.SessionId == sessionId);
                Check("stored ExpectedAmount matches drawer math", stored.ExpectedAmount == summary.Data.ExpectedCash,
                    $"db={stored.ExpectedAmount} calc={summary.Data.ExpectedCash}");

                var reclose = await sessions.CloseSessionAsync(sessionId, 0, cashier.UserId);
                Check("double-close rejected", !reclose.IsSuccess);
            }
        }

        // ───────────────── 6.5 Financial guards ATTACKS (VULN-01/02/06/08) ─────────────────
        // كل حالة هنا هجوم موثق في ATTACK_REPORT.md — الدفاع الجديد يجب أن يرفضه.
        private static async Task FinancialGuardsAttackCycle(int sessionId)
        {
            Console.WriteLine("--- FINANCIAL GUARDS ATTACKS ---");
            using var s = Scope();
            var checkout = Svc<clsCheckoutService>(s);
            var returns = Svc<clsReturnService>(s);
            var purchase = Svc<clsPurchaseService>(s);
            var sessions = Svc<clsPosSessionService>(s);
            var db = Svc<Sayl.Core.Data.SaylDbContext>(s);
            var admin = await db.Users.FirstAsync(u => u.Username == "admin");
            var cashier = await db.Users.FirstAsync(u => u.Username == "cashier1");

            // B3: الاختبارات المالية تعتمد درجًا مفتوحًا حقيقيًا — جلسة الكاشير القديمة
            // أُغلقت في CashDeskCycle، فنعيد فتح درج للمدير ونعيد توجيه sessionId إليه.
            var acting = await sessions.GetOpenSessionByUserAsync(admin.UserId);
            if (acting.IsSuccess && acting.Data != null)
            {
                sessionId = acting.Data.SessionId;
            }
            else
            {
                var newActing = await sessions.OpenSessionAsync(admin.UserId, 0m);
                if (newActing.IsSuccess && newActing.Data > 0)
                    sessionId = newActing.Data;
            }

            // VULN-02: خصم مبلغ يتجاوز الإجمالي → صافٍ سالب «مدفوع من العدم»
            var overDiscount = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = admin.UserId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 1 } },
                DiscountAmount = 1_000_000m,
                Payments = new List<DtoPaymentInfo>()
            });
            Check("ATTACK over-discount rejected", !overDiscount.IsSuccess, $"'{overDiscount.Message}'");

            // VULN-02: نسبة خصم سالبة
            var negDiscount = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = admin.UserId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 1 } },
                DiscountPercent = -10m,
                Payments = new List<DtoPaymentInfo> { new() { Amount = 100m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
            });
            Check("ATTACK negative discount rejected", !negDiscount.IsSuccess, $"'{negDiscount.Message}'");

            // VULN-02: ضريبة 50000% (كانت تمر حتى للكاشير — ديون وهمية)
            var crazyTax = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = cashier.UserId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 1 } },
                TaxPercent = 50_000m,
                Payments = new List<DtoPaymentInfo> { new() { Amount = 100m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
            });
            Check("ATTACK 50000% tax rejected", !crazyTax.IsSuccess, $"'{crazyTax.Message}'");

            // VULN-02b: دفع يتجاوز الصافي (كانت 400 تختفي من الدفاتر)
            var overpay = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = admin.UserId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 1 } },
                Payments = new List<DtoPaymentInfo> { new() { Amount = 10_000m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
            });
            Check("ATTACK overpayment rejected", !overpay.IsSuccess, $"'{overpay.Message}'");

            // VULN-01: مرتجع ببنود مزدوجة — {X:5},{X:5} على مباع 8
            var probeSale = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = admin.UserId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 8 } },
                Payments = new List<DtoPaymentInfo> { new() { Amount = 12.50m * 8, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
            });
            Check("ATTACK probe sale (8 units)", probeSale.IsSuccess, probeSale.Message);
            if (probeSale.IsSuccess)
            {
                var dupReturn = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
                {
                    OriginalInvoiceId = probeSale.Data,
                    UserId = admin.UserId,
                    Items = new List<DtoReturnItem>
                    {
                        new() { ProductId = 1, Quantity = 5 },
                        new() { ProductId = 1, Quantity = 5 }
                    }
                });
                Check("ATTACK duplicate-line return rejected", !dupReturn.IsSuccess, $"'{dupReturn.Message}'");

                // والمرتجع الصحيح المفرد يبقى مقبولًا
                var okReturn = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
                {
                    OriginalInvoiceId = probeSale.Data,
                    UserId = admin.UserId,
                    Items = new List<DtoReturnItem> { new() { ProductId = 1, Quantity = 3 } }
                });
                Check("legit single return still allowed", okReturn.IsSuccess, okReturn.Message);
            }

            // VULN-08: كاشير يكمل معلقة أدمن → مرفوض؛ المالك يكملها → مقبول
            var suspForeign = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = admin.UserId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 1 } }
            }, suspend: true);
            Check("ATTACK foreign-resume setup", suspForeign.IsSuccess, suspForeign.Message);
            if (suspForeign.IsSuccess)
            {
                var foreignComplete = await checkout.CompleteSuspendedInvoiceAsync(suspForeign.Data,
                    new List<DtoPaymentInfo> { new() { Amount = 20m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } },
                    actorUserId: cashier.UserId);
                Check("ATTACK foreign resume rejected", !foreignComplete.IsSuccess, $"'{foreignComplete.Message}'");

                var ownComplete = await checkout.CompleteSuspendedInvoiceAsync(suspForeign.Data,
                    new List<DtoPaymentInfo> { new() { Amount = 12.50m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } },
                    actorUserId: admin.UserId,
                    actorSessionId: sessionId); // VULN-05: درج الفعل
                Check("own resume allowed", ownComplete.IsSuccess, ownComplete.Message);

                // VULN-05: دفعة الإكمال موجهة لدرج الجلسة التي أكملت (لا جلسة التعليق)
                if (ownComplete.IsSuccess)
                {
                    var paySession = await VerifyAsync(vdb =>
                        vdb.Payments.AsNoTracking()
                            .Where(p => p.InvoiceId == suspForeign.Data && p.PaymentMethod == "cash")
                            .Select(p => p.SessionId).FirstOrDefaultAsync());
                    Check("resume payment attributed to acting session", paySession == sessionId,
                        $"paySession={paySession} expected={sessionId}");
                }
            }

            // VULN-05: مرتجع اليوم يوجّه نقد الاسترداد لدرج جلسة الفعل
            // (نبني فاتورة بيع بجلسة أخرى ثم نرتجع بجلسة اليوم ونفحص التوجيه)
            var retSession = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = admin.UserId,
                SessionId = sessionId, // بيع اليوم بجلسة اليوم — المرتجع من نفس الجلسة
                Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 1 } },
                Payments = new List<DtoPaymentInfo> { new() { Amount = 12.50m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
            });
            Check("VULN-05 probe sale in session", retSession.IsSuccess, retSession.Message);
            if (retSession.IsSuccess)
            {
                var ret = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
                {
                    OriginalInvoiceId = retSession.Data,
                    UserId = admin.UserId,
                    SessionId = sessionId, // درج الفعل
                    Items = new List<DtoReturnItem> { new() { ProductId = 1, Quantity = 1 } }
                });
                Check("VULN-05 return executed", ret.IsSuccess, ret.Message);
                if (ret.IsSuccess)
                {
                    var refundSession = await VerifyAsync(vdb =>
                        vdb.Payments.AsNoTracking()
                            .Where(p => p.InvoiceId == ret.Data && p.Type == Common.Enums.enPaymentType.outcome)
                            .Select(p => p.SessionId).FirstOrDefaultAsync());
                    Check("refund attributed to acting session", refundSession == sessionId,
                        $"refundSession={refundSession} expected={sessionId}");
                }
            }

            // VULN-45 (ب): كل طابع جديد يُخزن UTC — والفرق بين
            // طابع الفاتورة والآن-UTC لا يتجاوز دقائق معدودة (ليس 3 ساعات).
            if (_saleInvoiceId > 0)
            {
                var stamp = await VerifyAsync(vdb =>
                    vdb.Invoices.Where(i => i.InvoiceId == _saleInvoiceId).Select(i => i.CreatedAt).FirstAsync());
                double gapMinutes = Math.Abs((DateTime.UtcNow - (stamp ?? DateTime.UtcNow)).TotalMinutes);
                Check("VULN-45 stamps stored as UTC", gapMinutes < 5,
                    $"gap={gapMinutes:0.0}min (لو ~180 = ما زال محليًا!)");
            }

            // VULN-03 (الحل ب): الاسترداد النسبي — فاتورة بخصم وضريبة يُرد
            // ما دُفع فعلًا لا الخام: بيع 100 بخصم 10% وضريبة 10% => صافي 99.
            // إرجاع نصف البضاعة => استرداد 49.50 (نصف الصافي) لا 50 الخام.
            {
                var discSale = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
                {
                    UserId = admin.UserId,
                    SessionId = sessionId,
                    Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 10, UnitPrice = 10m } },
                    DiscountPercent = 10m,          // خصم 10
                    TaxPercent = 10m,                // ضريبة 10 على الباقي
                    Payments = new List<DtoPaymentInfo> { new() { Amount = 99m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
                });
                Check("VULN-03 probe sale (net 99: 100-10%+10% tax)", discSale.IsSuccess, discSale.Message);
                if (discSale.IsSuccess)
                {
                    // صافي الفاتورة = 100 - 10 + 9 = 99 (تحقق المعادلة الأساسية)
                    var netCheck = await VerifyAsync(vdb =>
                        vdb.Invoices.Where(i => i.InvoiceId == discSale.Data).Select(i => i.NetAmount).FirstAsync());
                    Check("VULN-03 sale net = 99.00", netCheck == 99m, $"net={netCheck}");

                    // إرجاع نصف الكمية (5) => الاسترداد النسبي = 5×10×(99/100) = 49.50
                    var halfRet = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
                    {
                        OriginalInvoiceId = discSale.Data,
                        UserId = admin.UserId,
                        SessionId = sessionId,
                        Items = new List<DtoReturnItem> { new() { ProductId = 1, Quantity = 5 } }
                    });
                    Check("VULN-03 proportional return executed", halfRet.IsSuccess, halfRet.Message);
                    if (halfRet.IsSuccess)
                    {
                        var retNet = await VerifyAsync(vdb =>
                            vdb.Invoices.Where(i => i.InvoiceId == halfRet.Data).Select(i => i.NetAmount).FirstAsync());
                        Check("VULN-03 refund = 49.50 (ratio-applied)", retNet == -49.50m, $"retNet={retNet}");

                        // التفصيل التدقيقي: خام 50، خصم مسترد 5، ضريبة مستردة 4.5 => صافي 49.50
                        var (retGross, retDisc, retTax) = await VerifyAsync(vdb =>
                            vdb.Invoices.Where(i => i.InvoiceId == halfRet.Data)
                                .Select(i => new ValueTuple<decimal, decimal, decimal>(i.TotalAmount, i.DiscountAmount, i.TaxAmount)).FirstAsync());
                        Check("VULN-03 breakdown gross/-5.00/-4.50",
                            retGross == -50m && retDisc == -5m && retTax == -4.50m,
                            $"gross={retGross} disc={retDisc} tax={retTax}");

                        // الاسترداد النقدي = min(49.50, مدفوع 99) = 49.50 كاش (لا من رصيد)
                        var refundCash = await VerifyAsync(async vdb =>
                            await vdb.Payments.Where(p => p.InvoiceId == halfRet.Data && p.Type == Common.Enums.enPaymentType.outcome)
                                .SumAsync(p => (decimal?)p.Amount) ?? 0m);
                        Check("VULN-03 cash refund 49.50", refundCash == 49.50m, $"cash={refundCash}");
                    }
                }
            }

            // ══ إثبات أولوية الدَّين في تقسيم الاسترداد (نقد/دين) ══
            // فاتورة صافي 99: العميل دفع 40 نقدًا والباقي 59 دينًا على عميل مسجل.
            // إرجاع نصف البضاعة => استرداد نسبي 49.50 => كلّه تخفيض للدين (59 متاح)
            // ولا نقد خارج الدرج. الدين يُخفَّض أولًا، والنقد المسترد لا يتجاوز أبداً
            // ما تجاوز الدين — راجع التعليق الموثق في BuildRefundPlanAsync.
            {
                // عميل تجريبي بالدين: نستخدم customer 1 (أحمد — رصيده متاح)
                var debtSale = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
                {
                    UserId = admin.UserId,
                    SessionId = sessionId,
                    CustomerId = 1,
                    Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 10, UnitPrice = 10m } },
                    DiscountPercent = 10m,
                    TaxPercent = 10m,
                    Payments = new List<DtoPaymentInfo> { new() { Amount = 40m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
                });
                Check("cash-problem probe: partial credit sale (40 of 99)", debtSale.IsSuccess, debtSale.Message);
                if (debtSale.IsSuccess)
                {
                    decimal balBefore = await VerifyAsync(vdb =>
                        vdb.Customers.Where(c => c.PartyId == 1).Select(c => c.Balance ?? 0m).FirstAsync());

                    var halfRet = await returns.CreateCustomerReturnAsync(new DtoReturnRequest
                    {
                        OriginalInvoiceId = debtSale.Data,
                        UserId = admin.UserId,
                        SessionId = sessionId,
                        Items = new List<DtoReturnItem> { new() { ProductId = 1, Quantity = 5 } }
                    });
                    Check("cash-problem return executed", halfRet.IsSuccess, halfRet.Message);
                    if (halfRet.IsSuccess)
                    {
                        // الدَّين أولًا: المستحق 59 > الاسترداد 49.50 ⇒ كله تخفيض دين
                        // ولا نقد خارج الدرج (القديم كان يرد 40 كاش ويترك دين 49.50)
                        var refundCash = await VerifyAsync(async vdb =>
                            await vdb.Payments.Where(p => p.InvoiceId == halfRet.Data && p.Type == Common.Enums.enPaymentType.outcome)
                                .SumAsync(p => (decimal?)p.Amount) ?? 0m);
                        Check("cash-problem refund is debt-first (cash 0)", refundCash == 0m, $"cash={refundCash}");

                        decimal balAfter = await VerifyAsync(vdb =>
                            vdb.Customers.Where(c => c.PartyId == 1).Select(c => c.Balance ?? 0m).FirstAsync());
                        // الرصيد كان 59 (دين) => يخفض 49.50 => 9.50
                        Check("cash-problem debt reduced by 49.50", balAfter == balBefore - 49.50m,
                            $"{balBefore}->{balAfter}");

                        // تنظيف أثر الاختبار: إعادة الرصيد لما قبل الجولة (الدين التجريبي مسموح بقاؤه)
                        using (var cs = Scope())
                        {
                            var cdb = Svc<Sayl.Core.Data.SaylDbContext>(cs);
                            var cust = await cdb.Customers.FindAsync(1);
                            if (cust != null) { cust.Balance = balBefore; await cdb.SaveChangesAsync(); }
                        }
                    }
                }
            }

            // VULN-06: خصم شراء يتجاوز الإجمالي → رصيد مورد سالب
            var badPurchase = await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
            {
                SupplierId = 13,
                UserId = admin.UserId,
                Items = new List<DtoPurchaseItem> { new() { ProductId = 1, UnitCost = 5m, Quantity = 2, BatchNumber = "ATK-" + DateTime.Now.Ticks % 100000 } },
                DiscountAmount = 1_000_000m
            });
            Check("ATTACK purchase over-discount rejected", !badPurchase.IsSuccess, $"'{badPurchase.Message}'");

            // VULN-26: كاشير ينشئ فاتورة شراء → مرفوض خدميًا (كان مفتوحًا للجميع)
            var cashierPurchase = await purchase.CreatePurchaseInvoiceAsync(new DtoPurchaseRequest
            {
                SupplierId = 13,
                UserId = cashier.UserId,
                Items = new List<DtoPurchaseItem> { new() { ProductId = 1, UnitCost = 5m, Quantity = 1, BatchNumber = "ATK-ROLE-" + DateTime.Now.Ticks % 100000 } }
            });
            Check("ATTACK cashier purchase rejected", !cashierPurchase.IsSuccess, $"'{cashierPurchase.Message}'");

            // B3: إغلاق درج الاختبار المفتوح لهذه الدورة ليبقى الهارنس قابلًا للتكرار
            if (sessionId > 0)
                await sessions.CloseSessionAsync(sessionId, 0m, admin.UserId);
        }

        // ───────────────── 6. Credit rules ─────────────────
        private static async Task CreditRulesCycle()
        {
            Console.WriteLine("--- CREDIT RULES ---");
            using var s = Scope();
            var checkout = Svc<clsCheckoutService>(s);
            var customers = Svc<clsCustomerService>(s);
            var db = Svc<Sayl.Core.Data.SaylDbContext>(s);
            var cashier = await db.Users.FirstAsync(u => u.Username == "cashier1");

            var custs = await customers.GetCustomersListAsync("");
            if (custs.Count == 0 || custs[0].Party.PartyId <= 0)
            {
                Check("credit tests skipped (no customers)", false);
                return;
            }
            int customerId = custs[0].Party.PartyId;

            // underpayment without customer → must fail
            var noCust = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = cashier.UserId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 1 } },
                Payments = new List<DtoPaymentInfo> { new() { Amount = 0.01m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
            });
            Check("underpay w/o customer rejected", !noCust.IsSuccess, $"'{noCust.Message}'");

            // partial payment with customer → allowed, balance increases
            var balBefore = await db.Customers.Where(c => c.PartyId == customerId).Select(c => c.Balance).FirstAsync();
            var partial = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = cashier.UserId,
                CustomerId = customerId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 1 } },
                Payments = new List<DtoPaymentInfo> { new() { Amount = 1m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
            });
            Check("partial credit sale with customer allowed", partial.IsSuccess, partial.Message);

            if (partial.IsSuccess)
            {
                var balAfter = await db.Customers.Where(c => c.PartyId == customerId).Select(c => c.Balance).FirstAsync();
                Check("customer balance increased by debt", balAfter == balBefore + (12.50m * 1.0m - 1m),
                    $"{balBefore}->{balAfter}");

                // cleanup: restore balance so tests stay idempotent-ish
                var cust = await db.Customers.FirstAsync(c => c.PartyId == customerId);
                cust.Balance = balBefore;
                await db.SaveChangesAsync();
            }
        }

        // ───────────────── 7. Permission rules ─────────────────
        private static async Task PermissionRulesCycle()
        {
            Console.WriteLine("--- PERMISSIONS ---");
            using var s = Scope();
            var checkout = Svc<clsCheckoutService>(s);
            var stock = Svc<clsStockService>(s);
            var expenses = Svc<clsExpenseService>(s);
            var products = Svc<clsProductService>(s);
            var db = Svc<Sayl.Core.Data.SaylDbContext>(s);
            var cashier = await db.Users.FirstAsync(u => u.Username == "cashier1");
            var admin = await db.Users.FirstAsync(u => u.Username == "admin");

            // cashier big discount rejected
            var bigDiscount = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = cashier.UserId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 1 } },
                DiscountPercent = 50,
                Payments = new List<DtoPaymentInfo> { new() { Amount = 100m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
            });
            Check("cashier >10% discount rejected", !bigDiscount.IsSuccess, bigDiscount.Message);

            // admin same discount OK
            // (VULN-02b: الدفع الآن محدود بالصافي — الاختبار القديم كان يدفع 100
            //  على صافٍ 6.25 أي يمرر دفعًا زائدًا فاسدًا؛ صححناه ليؤدي الصافي تمامًا)
            var adminDiscount = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = admin.UserId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 1 } },
                DiscountPercent = 50,
                Payments = new List<DtoPaymentInfo> { new() { Amount = 6.25m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
            });
            Check("admin big discount allowed", adminDiscount.IsSuccess, adminDiscount.Message);

            // cashier stock adjust rejected, admin allowed
            var adjCashier = await stock.AdjustStockAsync(new DtoStockAdjustmentRequest
            {
                UserId = cashier.UserId, ProductId = 1, Quantity = 1, IsIncrease = true, Reason = "test"
            });
            Check("cashier stock adjust rejected", !adjCashier.IsSuccess, adjCashier.Message);

            var adjAdmin = await stock.AdjustStockAsync(new DtoStockAdjustmentRequest
            {
                UserId = admin.UserId, ProductId = 1, Quantity = 1, IsIncrease = true, Reason = "test-harness"
            });
            Check("admin stock adjust allowed", adjAdmin.IsSuccess, adjAdmin.Message);

            // expense delete requires accountant+
            var expAdd = await expenses.AddNewExpenseAsync(new DtoExpense
            {
                Description = "اختبار", Amount = 5m, ExpenseDate = DateOnly.FromDateTime(DateTime.Today),
                UserId = admin.UserId, Category = "اختبار"
            });
            Check("expense added", expAdd.IsSuccess, expAdd.Message);

            if (expAdd.IsSuccess)
            {
                var delCashier = await expenses.DeleteExpenseAsync(expAdd.Data, cashier.UserId);
                Check("cashier delete-expense rejected", !delCashier.IsSuccess, delCashier.Message);

                var delAdmin = await expenses.DeleteExpenseAsync(expAdd.Data, admin.UserId);
                Check("admin delete-expense allowed", delAdmin.IsSuccess, delAdmin.Message);
            }

            // deactivate/reactivate needs accountant+
            var deactCashier = await products.DeactivateProductAsync(2, cashier.UserId);
            Check("cashier deactivate-product rejected", !deactCashier.IsSuccess);
        }

        // ───────────────── 7b. Bitmask permission system ─────────────────
        // نظام البتات الجديد (انظر AppPermissions): يثبت العقد كاملًا —
        // القيم الثابتة، الحماية الهرمية، السقف 3071، حماية الأدوار
        // الأساسية، والدخول بدور معطل.
        private static async Task BitmaskPermissionsCycle()
        {
            Console.WriteLine("--- BITMASK PERMISSIONS ---");
            using var s = Scope();
            var users = Svc<clsUsersService>(s);
            var roles = Svc<clsRoleService>(s);
            var db = Svc<Sayl.Core.Data.SaylDbContext>(s);

            var admin = await db.Users.FirstAsync(u => u.Username == "admin");
            var cashier = await db.Users.FirstAsync(u => u.Username == "cashier1");

            // 1) ثبات الأدوار الأساسية بعد الترقية
            var adminRole = await db.Roles.FirstAsync(r => r.Name == "مدير النظام");
            var cashierRole = await db.Roles.FirstAsync(r => r.Name == "كاشير");
            var accountantRole = await db.Roles.FirstAsync(r => r.Name == "محاسب");
            Check("admin role value = 4095", adminRole.RoleValue == AppPermissions.AdminRoleValue, $"got {adminRole.RoleValue}");
            Check("cashier role value = 0", cashierRole.RoleValue == AppPermissions.CashierRoleValue, $"got {cashierRole.RoleValue}");
            Check("accountant role value = 508", accountantRole.RoleValue == AppPermissions.AccountantRoleValue, $"got {accountantRole.RoleValue}");

            // 2) الرياضيات العقدية: 508 = كل الشاشات إلا (منتجات/مخزون/مستخدمون/أدوار/إعدادات)
            int expected508 = AppPermissions.Customers | AppPermissions.Suppliers |
                AppPermissions.SalesInvoices | AppPermissions.PurchaseInvoices |
                AppPermissions.Expenses | AppPermissions.Reports | AppPermissions.Employees;
            Check("508 = accountant mask (all but admin/stock)", expected508 == 508, $"computed {expected508}");
            Check("accountant lacks products bit", !AppPermissions.Has(508, AppPermissions.Products));
            Check("accountant lacks roles bit", !AppPermissions.Has(508, AppPermissions.Roles));
            Check("accountant has reports bit", AppPermissions.Has(508, AppPermissions.Reports));

            // 3) الحماية الهرمية: أدمن لا يُسند أدمن (تغيير دور لحساب مدير من مدير = ممنوع بالخدمة)
            //    (المشرف 3071 لا يُخترع هنا — نستخدم محاولة تسكين دور المدير لنفسه)
            var selfToAdmin = await users.ChangeRoleAsync(admin.UserId, adminRole.RoleId, actorUserId: admin.UserId);
            Check("self keep-role is no-op rejected", !selfToAdmin.IsSuccess);

            // 4) السقف 3071: دور ببت الأدوار (1024) يُرفض خدميًا
            var badRole = await roles.AddNewRoleAsync(new DtoRole
            {
                Name = "دور اختبار بت الأدوار",
                RoleValue = AppPermissions.Customers | AppPermissions.Roles
            }, admin.UserId);
            Check("role with Roles-bit rejected", !badRole.IsSuccess, badRole.Message);

            // 5) الدور الفارغ مرفوض
            var emptyRole = await roles.AddNewRoleAsync(new DtoRole
            {
                Name = "دور فارغ اختبار",
                RoleValue = 0
            }, admin.UserId);
            Check("empty role rejected", !emptyRole.IsSuccess, emptyRole.Message);

            // 6) دور صالح (عملاء فقط = 4) ثم تعديله وحمايته
            var okRole = await roles.AddNewRoleAsync(new DtoRole
            {
                Name = "دور اختبار مؤقت",
                RoleValue = AppPermissions.Customers
            }, admin.UserId);
            Check("custom role (customers only) created", okRole.IsSuccess, okRole.Message);

            if (okRole.IsSuccess)
            {
                // 7) الأدوار الأساسية لا تُعدَّل
                var editAdmin = await roles.UpdateRoleAsync(new DtoRole
                {
                    RoleId = adminRole.RoleId, Name = "مدير النظام", RoleValue = AppPermissions.CustomRoleCeiling
                }, admin.UserId);
                Check("admin role edit rejected", !editAdmin.IsSuccess, editAdmin.Message);

                // 8) الأدوار الأساسية لا تُعطَّل
                var deactCashierRole = await roles.DeactivateRoleAsync(cashierRole.RoleId, admin.UserId);
                Check("cashier role deactivate rejected", !deactCashierRole.IsSuccess, deactCashierRole.Message);

                // 9) تعطيل دور حر ثم فشل دخوله
                var deact = await roles.DeactivateRoleAsync(okRole.Data, admin.UserId);
                Check("custom role deactivated", deact.IsSuccess, deact.Message);

                // 10) حساب بدور معطل يُرفض دخوله (Fail-Closed)
                var probeUser = new NewUserRequest("probe_disabled", "Probe12345", okRole.Data, null);
                var probeAdd = await users.AddNewUserAsync(probeUser, admin.UserId);
                Check("probe user on disabled role rejected", !probeAdd.IsSuccess, probeAdd.Message);

                // 11) التنظيف: حذف دور الاختبار الفعلي (غير مستخدم)
                var delRole = await roles.DeleteRoleAsync(okRole.Data, admin.UserId);
                Check("test role hard-deleted (unused)", delRole.IsSuccess, delRole.Message);
            }

            // 12) كلمة المرور الذاتية من شاشة الدخول: قديمة خاطئة ترفض، صحيحة تنجح
            var wrongOld = await users.ChangePasswordAsync("cashier1", "WrongOld99", "NewCashier123");
            Check("self change wrong-old rejected", !wrongOld.IsSuccess);
            var selfChange = await users.ChangePasswordAsync("cashier1", "Cashier123", "Cashier456");
            Check("self change with correct old works", selfChange.IsSuccess, selfChange.Message);
            if (selfChange.IsSuccess)
            {
                // إعادة لكلمة الاختبار القياسية لبقية الجنيات
                await users.ResetPasswordAsync(cashier.UserId, "Cashier123", admin.UserId);
            }

            // 13) الكاشير (0) لا يفتح شيئًا بالبت — رياضيات العقد
            Check("cashier mask opens nothing by bit",
                !AppPermissions.Has(0, AppPermissions.Products) &&
                !AppPermissions.Has(0, AppPermissions.Reports) &&
                !AppPermissions.Has(0, AppPermissions.Settings));

            // 14) الحماية: كاشير لا يعيد تعيين كلمات الآخرين حتى بصلاحية صحيحة
            var cashierReset = await users.ResetPasswordAsync(admin.UserId, "Hacked12345", cashier.UserId);
            Check("cashier cannot reset admin password", !cashierReset.IsSuccess, cashierReset.Message);
        }

        // ───────────────── 8. Reports ─────────────────
        private static async Task ReportsCycle()
        {
            Console.WriteLine("--- REPORTS ---");
            using var s = Scope();
            var reports = Svc<clsReportService>(s);

            var profit = await reports.GetProfitSummaryAsync(DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today));
            Check("profit summary", profit.IsSuccess && profit.Data != null, profit.Message);
            if (profit.Data != null)
                Console.WriteLine($"   profit: revenue={profit.Data.SalesRevenue} cogs={profit.Data.COGS} profit={profit.Data.NetProfit}");

            var daily = await reports.GetDailySalesAsync(DateOnly.FromDateTime(DateTime.Today));
            Check("daily sales", daily.IsSuccess && daily.Data != null);

            var top = await reports.GetTopSellingProductsAsync(DateOnly.FromDateTime(DateTime.Today).AddDays(-30), DateOnly.FromDateTime(DateTime.Today), 5);
            Check("top selling", top.IsSuccess && top.Data != null);

            var inv = await reports.GetInventoryStatusAsync();
            Check("inventory status", inv.IsSuccess && inv.Data != null);

            var expiring = await reports.GetExpiringBatchesAsync(365);
            Check("expiring batches", expiring.IsSuccess && expiring.Data != null);

            var dash = await reports.GetDashboardSummaryAsync(DateOnly.FromDateTime(DateTime.Today));
            Check("dashboard summary", dash.IsSuccess && dash.Data != null, dash.Message);
        }

        // ───────────────── 9. Settings + backup ─────────────────
        private static async Task SettingsBackupCycle()
        {
            Console.WriteLine("--- SETTINGS + BACKUP ---");
            using var s = Scope();
            var settings = Svc<clsSettingsService>(s);
            var backup = Svc<clsBackupService>(s);

            await settings.SetAsync("HarnessTestKey", "hello-" + Guid.NewGuid().ToString("N")[..8]);
            var readBack = await settings.GetAsync("HarnessTestKey");
            Check("settings round-trip", readBack.StartsWith("hello-"));

            await settings.SetAsync(clsSettingsService.KeyTaxPercent, "15");
            var tax = await settings.GetDecimalAsync(clsSettingsService.KeyTaxPercent, 0m);
            Check("tax percent persisted", tax == 15m);
            await settings.SetAsync(clsSettingsService.KeyTaxPercent, "0"); // reset

            string tmp = Path.Combine(Path.GetTempPath(), "sayl-backup-test");
            var bk = await backup.BackupAsync(tmp, keepLast: 3);
            Check("backup creates .bak", bk.IsSuccess && File.Exists(bk.Data), bk.Message);
            if (bk.IsSuccess)
                Console.WriteLine($"   backup: {bk.Data} ({new FileInfo(bk.Data).Length / 1024} KB)");
        }

        // ───────────────── 10. CRUD smoke ─────────────────
        private static async Task CrudSmokeCycle()
        {
            Console.WriteLine("--- CRUD SMOKE ---");
            using var s = Scope();
            var products = Svc<clsProductService>(s);
            var db = Svc<Sayl.Core.Data.SaylDbContext>(s);
            var admin = await db.Users.FirstAsync(u => u.Username == "admin");

            var created = await products.AddNewProductAsync(new DtoProduct
            {
                Name = "منتج اختبار آلي " + DateTime.Now.Ticks % 100000,
                Barcode = "TEST" + DateTime.Now.Ticks % 1000000000,
                BasePrice = 10m, CostPrice = 5m, Type = "stock", ReorderLevel = 5,
                Unit = "قطعة", IsActive = true
            }, admin.UserId);
            Check("product create", created.IsSuccess && created.Data > 0, created.Message);

            if (!created.IsSuccess || created.Data <= 0) return;

            var upd = await products.UpdateProductAsync(new DtoProduct
            {
                ProductId = created.Data, Name = "محدَّث اختبار", BasePrice = 12m,
                CostPrice = 6m, Type = "stock", ReorderLevel = 3, Unit = "قطعة"
            }, admin.UserId);
            Check("product update", upd.IsSuccess, upd.Message);

            var deact = await products.DeactivateProductAsync(created.Data, admin.UserId);
            Check("product deactivate", deact.IsSuccess, deact.Message);

            var act = await products.ActivateProductAsync(created.Data);
            Check("product reactivate", act.IsSuccess, act.Message);
        }

        // ───────────────── 11. تعادل الدفعات والمخزون (خطأ خبز البرجر) ─────────────────
        // التسوية بلا رقم دفعة يجب أن تصنع دفعة تلقائيًا — وإلا يبقى المخزون
        // «غير قابل للبيع»: FEFO يرفض رغم وجود كمية في Inventory.
        private static async Task AdjustBatchConsistencyCycle()
        {
            Console.WriteLine("--- ADJUST/BATCH CONSISTENCY ---");
            using var s = Scope();
            var products = Svc<clsProductService>(s);
            var stock = Svc<clsStockService>(s);
            var db = Svc<Sayl.Core.Data.SaylDbContext>(s);
            var admin = await db.Users.FirstAsync(u => u.Username == "admin");

            var created = await products.AddNewProductAsync(new DtoProduct
            {
                Name = "اختبار دفعة تلقائية " + DateTime.Now.Ticks % 100000,
                Barcode = "TESTB" + DateTime.Now.Ticks % 1000000000,
                BasePrice = 3m, CostPrice = 1m, Type = "stock", Unit = "قطعة", IsActive = true
            }, admin.UserId);
            Check("recon product create", created.IsSuccess && created.Data > 0, created.Message);
            if (!created.IsSuccess || created.Data <= 0) return;
            int pid = created.Data;

            var adj = await stock.AdjustStockAsync(new DtoStockAdjustmentRequest
            {
                ProductId = pid,
                Quantity = 5,
                IsIncrease = true,
                Reason = "هارنس: زيادة بلا رقم دفعة",
                UserId = admin.UserId
            });
            Check("adjust +5 without batch number succeeds", adj.IsSuccess, adj.Message);

            int batchQty = await db.Batches.Where(b => b.ProductId == pid).SumAsync(b => (int?)b.BatchQuantity) ?? 0;
            Check("auto batch created with quantity", batchQty == 5, $"batchSum={batchQty}");

            // كان يفشل قبل الإصلاح: الدفعة الوحيدة تجميعية تلقائية.
            // نطاق منفصل — قارئ خصم البيع الحقيقي لا يحمل كاش الكيانات من التسوية
            using (var s2 = Scope())
            {
                var freshStock = Svc<clsStockService>(s2);
                var deduct = await freshStock.SellStockAsync(pid, 2);
                Check("sale-deduct 2 from auto batch succeeds", deduct.IsSuccess, deduct.Message);
            }

            int after = await db.Batches.Where(b => b.ProductId == pid).SumAsync(b => (int?)b.BatchQuantity) ?? 0;
            Check("batches track inventory after deduct", after == 3, $"batchSum={after}");
        }
    }
}
