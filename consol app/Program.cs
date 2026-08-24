using Common.DTOs;
using Common.DTOs.RequestModels;
using Common.DTOs.ViewModel;
using Common.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using DependencyInjection;
using Sayl.Core.Services;

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
            Services = new ServiceCollection()
                .AddSaylServices("Server=.;Database=SaylDB;Trusted_Connection=True;TrustServerCertificate=True;")
                .BuildServiceProvider();

            await AuthCycle();
            var (sessionId, cashierId) = await SessionAndSaleCycle();
            if (sessionId > 0)
            {
                await SuspendResumeCancelCycle(sessionId, cashierId);
                await ReturnCycle();
                await CashDeskCycle(sessionId);
            }
            await CreditRulesCycle();
            await PermissionRulesCycle();
            await ReportsCycle();
            await SettingsBackupCycle();
            await CrudSmokeCycle();

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

            // إعادة تعيين كلمات مرور مستخدمي الاختبار إلى قيم معروفة
            var cashier1 = await db.Users.FirstAsync(u => u.Username == "cashier1");
            await users.ResetPasswordAsync(cashier1.UserId, "Cashier123");

            string[] candidates = { "admin123", "Admin@123", "12345678", "admin1234", "password1" };
            Result<UserVM>? adminLogin = null;
            foreach (var pwd in candidates)
            {
                adminLogin = await users.LoginAsync("admin", pwd);
                if (adminLogin.IsSuccess) { Console.WriteLine($"(admin password found: '{pwd}')"); break; }
            }
            Check("admin login", adminLogin?.IsSuccess == true);

            var cashierLogin = await users.LoginAsync("cashier1", "Cashier123");
            Check("cashier1 login (after reset)", cashierLogin.IsSuccess, cashierLogin.Message);

            var bad = await users.LoginAsync("admin", "definitely-wrong-999");
            Check("wrong password rejected", !bad.IsSuccess);

            var unknown = await users.LoginAsync("no-such-user", "whatever123");
            Check("unknown user rejected", !unknown.IsSuccess);
        }

        // ───────────────── 2. Session + direct sale ─────────────────
        private static int _saleInvoiceId;

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
                await sessions.CloseSessionAsync(existing.Data.SessionId, 0);

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
            });
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

                var closed = await sessions.CloseSessionAsync(sessionId, summary.Data.ExpectedCash);
                Check("close session Z", closed.IsSuccess, closed.Message);

                var stored = await db.PosSessions.AsNoTracking().FirstAsync(p => p.SessionId == sessionId);
                Check("stored ExpectedAmount matches drawer math", stored.ExpectedAmount == summary.Data.ExpectedCash,
                    $"db={stored.ExpectedAmount} calc={summary.Data.ExpectedCash}");

                var reclose = await sessions.CloseSessionAsync(sessionId, 0);
                Check("double-close rejected", !reclose.IsSuccess);
            }
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
            var adminDiscount = await checkout.ProcessCheckoutAsync(new DtoCheckoutRequest
            {
                UserId = admin.UserId,
                Items = new List<DtoCheckoutItem> { new() { ProductId = 1, Quantity = 1 } },
                DiscountPercent = 50,
                Payments = new List<DtoPaymentInfo> { new() { Amount = 100m, Method = "cash", PaymentDate = DateOnly.FromDateTime(DateTime.Today) } }
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
            });
            Check("product create", created.IsSuccess && created.Data > 0, created.Message);

            if (!created.IsSuccess || created.Data <= 0) return;

            var upd = await products.UpdateProductAsync(new DtoProduct
            {
                ProductId = created.Data, Name = "محدَّث اختبار", BasePrice = 12m,
                CostPrice = 6m, Type = "stock", ReorderLevel = 3, Unit = "قطعة"
            });
            Check("product update", upd.IsSuccess, upd.Message);

            var deact = await products.DeactivateProductAsync(created.Data, admin.UserId);
            Check("product deactivate", deact.IsSuccess, deact.Message);

            var act = await products.ActivateProductAsync(created.Data);
            Check("product reactivate", act.IsSuccess, act.Message);
        }
    }
}
