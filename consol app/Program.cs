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
                .AddSaylServices("Server=DESKTOP-DT3B4CG\\SAYL;Database=SaylDB;Trusted_Connection=True;TrustServerCertificate=True;")
                .BuildServiceProvider();

            // مسبار التزويد: دورات الهارنس تستهلك مخزون المنتج 1 دورة بعد دورة
            // (بيع/تعليق/مرتجع...) — نعيد 500 وحدة بدمغة وقتية فريدة قبل كل جولة
            // كي تبقى الاختبارات قابلة للتكرار إلى ما لا نهاية (idempotent).
            using (var ws = Scope())
            {
                var stock = Svc<clsStockService>(ws);
                await stock.ReceiveStockAsync(new DtoStockReceiveRequest
                {
                    ProductId = 1,
                    BatchNumber = "HARNESS-" + DateTime.Now.Ticks,
                    Quantity = 500
                });
            }

            await AuthCycle();
            var (sessionId, cashierId) = await SessionAndSaleCycle();
            if (sessionId > 0)
            {
                await SuspendResumeCancelCycle(sessionId, cashierId);
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
            // (الإعادة للآخرين: بوابة المدير — نمرر فاعلًا أدمن)
            var cashier1 = await db.Users.FirstAsync(u => u.Username == "cashier1");
            var adminUser = await db.Users.FirstAsync(u => u.Username == "admin");
            await users.ResetPasswordAsync(cashier1.UserId, "Cashier123", adminUser.UserId);

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

        // ───────────────── 6.5 Financial guards ATTACKS (VULN-01/02/06/08) ─────────────────
        // كل حالة هنا هجوم موثق في ATTACK_REPORT.md — الدفاع الجديد يجب أن يرفضه.
        private static async Task FinancialGuardsAttackCycle(int sessionId)
        {
            Console.WriteLine("--- FINANCIAL GUARDS ATTACKS ---");
            using var s = Scope();
            var checkout = Svc<clsCheckoutService>(s);
            var returns = Svc<clsReturnService>(s);
            var purchase = Svc<clsPurchaseService>(s);
            var db = Svc<Sayl.Core.Data.SaylDbContext>(s);
            var admin = await db.Users.FirstAsync(u => u.Username == "admin");
            var cashier = await db.Users.FirstAsync(u => u.Username == "cashier1");

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

            // ══ إثبات سلامة تقسيم الاسترداد نقد/دين (تحقق بعد شك ظاهري) ══
            // فاتورة صافي 99: العميل دفع 40 نقدًا والباقي 59 دينًا على عميل مسجل.
            // إرجاع نصف البضاعة => استرداد نسبي 49.50 => نقد 40 (حد المدفوع)
            // + 9.50 تخفض الدين. هذا هو التوازن الصحيح الوحيد: النقد المسترد
            // لا يتجاوز ما دخل الدرج، والدين المتبقي = قيمة البضاعة المُبقاة.
            // (المعادلة min(الاسترداد, المدفوع) — أُعيد التحقق فيها صراحة
            //  بعد شكّ ظاهري أثناء VULN-03، فثبت أنها سليمة دائمًا — راجع
            //  التعليق الموثق في ProcessCustomerRefundAsync.)
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
                        // النقد المسترد = 40 فقط (min(49.50, 40)) — والـ 9.50 الباقية تخفض الدين
                        var refundCash = await VerifyAsync(async vdb =>
                            await vdb.Payments.Where(p => p.InvoiceId == halfRet.Data && p.Type == Common.Enums.enPaymentType.outcome)
                                .SumAsync(p => (decimal?)p.Amount) ?? 0m);
                        Check("cash-problem cash refund capped at 40", refundCash == 40m, $"cash={refundCash}");

                        decimal balAfter = await VerifyAsync(vdb =>
                            vdb.Customers.Where(c => c.PartyId == 1).Select(c => c.Balance ?? 0m).FirstAsync());
                        // الرصيد كان 59 (دين) => يخفض 9.50 => 49.50
                        Check("cash-problem debt reduced by 9.50", balAfter == balBefore - 9.50m,
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
