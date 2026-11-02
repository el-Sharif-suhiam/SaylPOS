using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;

namespace Sayl.Core.Services
{
    /// <summary>
    /// تسوية مدفوعات الطرف على فواتيره المستحقة — السدادة الموحدة ضد التحصيل المزدوج.
    ///
    /// المشكلة: الدفعات العامة (InvoiceId = null: تحصيل من عميل / سداد لمورد) تخفض
    /// رصيد الطرف مباشرة بينما حساب «المتبقي» على فاتورة محددة كان يعتمد فقط على
    /// الدفعات المرتبطة بالفاتورة — فيمكن سداد الدَّين نفسه مرتين (مرة عامة ومرة على
    /// الفاتورة)، وتبقى حالة سداد الفاتورة (PaymentStatus) قديمة.
    ///
    /// الحل: تخصيص حتمي (deterministic) — الدفعات العامة تُخصَّص على الفواتير
    /// المستحقة الأقدم أولًا (CreatedAt ثم InvoiceId). نسحب الأحداث الخام من القاعدة
    /// في كل مرة (فواتير + مرتجعات + دفعات) فلا نحتاج جدول تسوية إضافيًا ولا تتأثر
    /// النتيجة بترتيب الاستدعاءات — نفس المدخلات تعطي نفس التخصيص دائمًا.
    ///
    /// الفائض عن مجموع المستحق يبقى رصيدًا غير مخصَّص (unapplied credit) في حساب الطرف.
    /// </summary>
    public static class clsPaymentAllocation
    {
        /// <summary>سطر مستحقات فاتورة للطرف بعد المرتجعات وكل أشكال السداد.</summary>
        public sealed class InvoiceOutstanding
        {
            public int InvoiceId { get; init; }
            /// <summary>صافي الفاتورة بعد خصم مرتجعاتها (المطلوب فعليًا).</summary>
            public decimal EffectiveNet { get; init; }
            /// <summary>دفعات مرتبطة بالفاتورة مباشرة (InvoiceId = هذه الفاتورة).</summary>
            public decimal InvoiceLinkedPaid { get; init; }
            /// <summary>نصيب الفاتورة من الدفعات العامة (تخصيص الأقدم أولًا).</summary>
            public decimal AllocatedGeneral { get; init; }
            /// <summary>المتبقي الفعلي المستحق = EffectiveNet − Linked − Allocated.</summary>
            public decimal Outstanding { get; init; }
        }

        /// <summary>
        /// مستحقات كل فواتير البيع/الشراء المكتملة للطرف، مرتبةً بالأقدم أولًا،
        /// بعد خصم المرتجعات وتخصيص الدفعات العامة (مرتبطة وغير مرتبطة).
        /// </summary>
        public static async Task<List<InvoiceOutstanding>> GetPartyOutstandingAsync(
            SaylDbContext dbContext, char partyType, int partyId,
            int? excludeReturnInvoiceId = null)
        {
            // أنواع الدفعات التي تخفض دين هذا الطرف: تحصيل من عميل = دخل، سداد لمورد = خارج
            enPaymentType generalType = partyType == 'S' ? enPaymentType.outcome : enPaymentType.income;

            // 1) فواتير الطرف المكتملة (بيع/شراء فقط — المرتجعات تُحسب ضمن صافي الأصل)
            var invoices = await dbContext.Invoices
                .Where(i => i.Status == enInvoicesStatus.completed
                         && i.PartyType == partyType && i.PartyId == partyId
                         && (i.Type == enInvoicesType.sale || i.Type == enInvoicesType.purchase))
                .Select(i => new
                {
                    i.InvoiceId,
                    i.CreatedAt,
                    i.NetAmount,
                    // المرتجعات المرتبطة بهذه الفاتورة سالبة بالتصميم (VULN-07)
                    // excludeReturnInvoiceId: مرتجع قيد التخطيط (حُفظ داخل المعاملة
                    // لكن توزيع استرداده لم يُقرَّر بعد) لا يُحسب انخفاضًا للالتزام
                    Returns = dbContext.Invoices
                        .Where(r => r.OriginalInvoiceId == i.InvoiceId
                                 && r.InvoiceId != excludeReturnInvoiceId
                                 && r.Status == enInvoicesStatus.completed
                                 && (r.Type == enInvoicesType.customer_return
                                  || r.Type == enInvoicesType.supplier_return))
                        .Sum(r => (decimal?)r.NetAmount) ?? 0m,
                    LinkedPaid = dbContext.Payments
                        .Where(p => p.InvoiceId == i.InvoiceId)
                        .Sum(p => (decimal?)p.Amount) ?? 0m
                })
                .ToListAsync();

            if (invoices.Count == 0)
                return new List<InvoiceOutstanding>();

            // 2) الدفعات العامة للطرف (بلا فاتورة) — المرشَّح للتخصيص
            decimal generalCredit = await dbContext.Payments
                .Where(p => p.InvoiceId == null
                         && p.PartyType == partyType && p.PartyId == partyId
                         && p.Type == generalType)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;

            // 3) تخصيص حتمي: الأقدم أولًا (CreatedAt ثم InvoiceId كسر تعادل)
            var result = new List<InvoiceOutstanding>();
            foreach (var inv in invoices.OrderBy(i => i.CreatedAt ?? DateTime.MinValue).ThenBy(i => i.InvoiceId))
            {
                decimal effectiveNet = inv.NetAmount + inv.Returns; // المرتجعات سالبة
                decimal remaining = effectiveNet - inv.LinkedPaid;

                decimal allocated = 0m;
                if (remaining > 0m && generalCredit > 0m)
                {
                    allocated = Math.Min(remaining, generalCredit);
                    generalCredit -= allocated;
                }

                result.Add(new InvoiceOutstanding
                {
                    InvoiceId = inv.InvoiceId,
                    EffectiveNet = effectiveNet,
                    InvoiceLinkedPaid = inv.LinkedPaid,
                    AllocatedGeneral = allocated,
                    Outstanding = remaining - allocated
                });
            }

            return result;
        }

        /// <summary>
        /// المتبقي لكل فاتورة من فواتير محددة — بتخصيص حتمي مطابق لـ GetPartyOutstandingAsync.
        ///
        /// الدفعات العامة (بلا فاتورة) تُخصَّص على فواتير الطرف **كلها** الأقدم أولًا،
        /// لذا يُحسب المتبقي على كامل مجموعة فواتير كل طرف مالك (لا الفواتير المطلوبة
        /// وحدها) ثم يُرشَّح الناتج للمفاتيح المطلوبة — وإلا ظن تقرير أن دفعة اليوم
        /// سددت فاتورة اليوم بينما هي سددت دَينًا أقدم بالتخصيص الزمني.
        ///
        /// فواتير الزوار (PartyId = null) بلا دفعات عامة: المتبقي = الصافي + مرتجعاتها − دفعاتها المرتبطة.
        /// </summary>
        public static async Task<Dictionary<int, decimal>> GetOutstandingForInvoicesAsync(
            SaylDbContext dbContext, IEnumerable<int> invoiceIds)
        {
            var requested = invoiceIds.Distinct().ToList();
            var map = new Dictionary<int, decimal>();
            if (requested.Count == 0)
                return map;

            var seeds = await dbContext.Invoices
                .Where(i => requested.Contains(i.InvoiceId))
                .Select(i => new { i.InvoiceId, i.PartyType, i.PartyId })
                .ToListAsync();

            // أصحاب الأطراف: حساب المتبقي على كامل فواتير الطرف (التخصيص الزمني يحتاجها كلها)
            var partyKeys = seeds
                .Where(i => i.PartyId != null)
                .Select(i => (PartyType: i.PartyType ?? 'C', PartyId: i.PartyId!.Value))
                .Distinct()
                .ToList();

            foreach (var (partyType, partyId) in partyKeys)
            {
                var rows = await GetPartyOutstandingAsync(dbContext, partyType, partyId);
                foreach (var r in rows)
                    map[r.InvoiceId] = r.Outstanding;
            }

            // فواتير الزوار بلا طرف — لا دفعات عامة تُخصَّص لها
            var walkInIds = seeds.Where(i => i.PartyId == null).Select(i => i.InvoiceId).ToList();
            if (walkInIds.Count > 0)
            {
                var walkInRows = await dbContext.Invoices
                    .Where(i => walkInIds.Contains(i.InvoiceId))
                    .Select(i => new
                    {
                        i.InvoiceId,
                        i.NetAmount,
                        Returns = dbContext.Invoices
                            .Where(r => r.OriginalInvoiceId == i.InvoiceId
                                     && r.Status == enInvoicesStatus.completed
                                     && (r.Type == enInvoicesType.customer_return
                                      || r.Type == enInvoicesType.supplier_return))
                            .Sum(r => (decimal?)r.NetAmount) ?? 0m,
                        LinkedPaid = dbContext.Payments
                            .Where(p => p.InvoiceId == i.InvoiceId)
                            .Sum(p => (decimal?)p.Amount) ?? 0m
                    })
                    .ToListAsync();

                foreach (var r in walkInRows)
                    map[r.InvoiceId] = r.NetAmount + r.Returns - r.LinkedPaid;
            }

            return map.Where(kv => requested.Contains(kv.Key))
                      .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        /// <summary>
        /// أثر دفعة عامة جديدة (بعد إضافتها للسياق أو قبلها — تُخصَّص القيمة الممرَّرة فقط)
        /// على حالات سداد الفواتير: تخصيص الأقدم أولًا ثم تحديث PaymentStatus لكل فاتورة
        /// تغيّرت حالتها. يعيد الفائض غير المخصَّص (credit بلا فواتير مستحقة).
        /// تُستدعى داخل معاملة الطرف حتى تظل الحالات متسقة مع الرصيد.
        /// </summary>
        public static async Task<decimal> ApplyGeneralPaymentToInvoiceStatusesAsync(
            SaylDbContext dbContext, char partyType, int partyId, decimal amount)
        {
            if (amount <= 0m)
                return 0m;

            // المستحق قبل هذه الدفعة (دون أن تُحتسب هي — لم تُضف بعد أو أُضيفت عامة ولا تدخل الحساب)
            var rows = await GetPartyOutstandingAsync(dbContext, partyType, partyId);

            decimal creditLeft = amount;
            foreach (var row in rows)
            {
                if (creditLeft <= 0m)
                    break;
                if (row.Outstanding <= 0m)
                    continue;

                decimal applied = Math.Min(row.Outstanding, creditLeft);
                creditLeft -= applied;

                var invoice = await dbContext.Invoices.FindAsync(row.InvoiceId);
                if (invoice == null)
                    continue;

                decimal coveredAfter = (row.EffectiveNet - row.Outstanding) + applied;
                var newStatus = coveredAfter >= row.EffectiveNet
                    ? enPaymentStatus.paid
                    : enPaymentStatus.partial;

                // لا نلمس الفاتورة إن لم تتغير حالتها (تفادي حفظ بلا داعٍ)
                if (invoice.PaymentStatus != newStatus)
                    invoice.PaymentStatus = newStatus;
            }

            return creditLeft; // فائض بلا فواتير مستحقة → رصيد غير مخصَّص
        }
    }
}
