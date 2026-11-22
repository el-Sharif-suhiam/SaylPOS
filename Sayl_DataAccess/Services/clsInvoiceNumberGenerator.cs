using System.Data;
using System.Globalization;
using System.Text;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Storage;
using Sayl.Core.Data;

namespace Sayl.Core.Services
{
    /// <summary>
    /// مولّد الرقم التجاري السنوي للفواتير INV-YYYY-NNNNNN — مساعد ساكن بنمط
    /// <see cref="clsAudit"/> (بلا DI ولا واجهات): يأخذ <c>SaylDbContext</c>
    /// ويعمل داخل معاملة المستدعي.
    ///
    /// 📖 السياسة الموثقة:
    ///  • تسلسل واحد مشترك لكل أنواع الفواتير الأربعة (sale/purchase/customer_return/
    ///    supplier_return) — لا وسيط «النوع» أصلًا في التوقيع: هذه هي سياسة
    ///    التسلسل المشترك نفسها (تسلسل واحد لكل سنة برمجية).
    ///  • تصفير سنوي = صف عدّاد مستقل لكل سنة (dbo.InvoiceNumberCounters —
    ///    المفتاح NumberYear) لا منطق إعادة تعيين: كل سنة تبدأ من 000001.
    ///  • السنة مصدرها وحيد ProgramClock.Today.Year (UTC+3) — لا DateTime مباشر.
    ///  • القفل تطبيقي مسمّى sp_getapplock بمالك المعاملة (Transaction) ومفتاحه
    ///    «InvoiceNumber:YYYY» — عمر القفل عمر معاملة المستدعي نفسها: الـcommit
    ///    يُفرجه، والـrollback يُفرجه كذلك بلا أي تنظيف يدوي.
    ///  • لماذا داخل المعاملة إجباريًا؟ لأن العدّاد نفسه يُحدَّث داخل معاملة
    ///    المستدعي: لو تراجعت الفاتورة تراجع معها إسناد الرقم (لا يُستهلك رقم
    ///    لأثر تراجع)، والقفل يظل محتكرًا حتى نهاية العملية فلا يتخلل مولّدًا
    ///    آخر قراءة-ثم-كتابة. نداء بلا معاملة نشطة = رفض صريح قبل أي SQL.
    ///  • التكرار محجوب على مستويين: القفل التطبيقي (تسلسل المُولِّدين) و
    ///    UQ_Invoices_InvoiceNumber الفريد المُرشَّح (حاجز المحرك).
    /// </summary>
    public static class clsInvoiceNumberGenerator
    {
        /// <summary>مهلة انتظار القفل التطبيقي (مللي ثانية) — سخية كي تمر
        /// التخصيصات المتوازية جميعها ترتيبيًا بدل الفشل على ازدحام عابر.</summary>
        private const int LockTimeoutMs = 10000;

        /// <summary>
        /// يسند الرقم التجاري التالي INV-YYYY-NNNNNN: قفل تطبيقي بمفتاح السنة →
        /// تحديث العدّاد (وإدراجه إن غاب) → قراءة القيمة الجديدة — دفعة SQL
        /// واحدة باراميترية داخل معاملة المستدعي.
        ///
        /// يعيد <see cref="Common.Results.Result{T}.Fail"/> (برسالة عربية) إن:
        ///  • لم توجد معاملة نشطة على السياق (رفض قبل أي SQL)، أو
        ///  • فشل/انتهت مهلة القفل التطبيقي (ازدحام) — المعاملة تبقى بيد
        ///    المستدعي (يراجعها أو يتراجع عنها).
        /// لا يقرأ جدول Invoices أبدًا — العدّاد + الفهرس الفريد يكفيان.
        /// </summary>
        /// <param name="db">سياق المستدعي — يجب أن يكون داخل معاملة نشطة.</param>
        /// <param name="programYear">سنة العدّاد — الافتراضي ProgramClock.Today.Year
        /// (UTC+3). وسيلة حقن للاختبارات فقط؛ مستدعو الإنتاج لا يمررونها أبدًا.</param>
        public static async Task<Common.Results.Result<string>> NextAsync(
            SaylDbContext db, int? programYear = null)
        {
            // شرط المعاملة يُفحص من Database.CurrentTransaction (لا اتصال raw)
            // — الرفض قبل أي SQL أو قفل.
            if (db.Database.CurrentTransaction == null)
            {
                return Common.Results.Result<string>.Fail(
                    "يجب أن يُنادى المولّد داخل معاملة قاعدة بيانات نشطة");
            }

            int year = programYear ?? ProgramClock.Today.Year;

            // الدفعة الواحدة: قفل (مالك المعاملة) → تحديث العدّاد → إدراج إن
            // غاب الصف → قراءة القيمة الجديدة. RETURN بعد فشل القفل كي لا يُمس
            // العدّاد عند الازدحام. القفل والإسناد في المعاملة نفسها — القفل
            // بلا معاملة (Session) ليس خيارًا أصلًا هنا.
            string sql =
                "DECLARE @lock int;\n" +
                "EXEC @lock = sys.sp_getapplock\n" +
                "    @Resource = @resource, @LockMode = 'Exclusive',\n" +
                "    @LockOwner = 'Transaction', @LockTimeout = @lockTimeout;\n" +
                "IF @lock < 0\n" +
                "BEGIN\n" +
                "    SELECT CAST(-1 AS bigint) AS Value;\n" +
                "    RETURN;\n" +
                "END\n" +
                "UPDATE dbo.InvoiceNumberCounters\n" +
                "    SET LastIssuedNumber = LastIssuedNumber + 1,\n" +
                "        LastUpdatedAtUtc = SYSUTCDATETIME()\n" +
                "    WHERE NumberYear = @year;\n" +
                "IF @@ROWCOUNT = 0\n" +
                "    INSERT INTO dbo.InvoiceNumberCounters (NumberYear, LastIssuedNumber)\n" +
                "    VALUES (@year, 1);\n" +
                "SELECT CAST(LastIssuedNumber AS bigint) AS Value\n" +
                "    FROM dbo.InvoiceNumberCounters WHERE NumberYear = @year;";

            long seq;
            var conn = (SqlConnection)db.Database.GetDbConnection();
            using (var cmd = conn.CreateCommand())
            {
                // الالتحاق الإلزامي بمعاملة السياق — القفل والتحديث بمالك
                // المعاملة وإلا كُتبا خارجها (فخ الالتحاق الموثق).
                cmd.Transaction = (SqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
                cmd.CommandText = sql;
                cmd.Parameters.Add("@resource", SqlDbType.NVarChar, 64).Value =
                    new StringBuilder("InvoiceNumber:").Append(year).ToString();
                cmd.Parameters.Add("@lockTimeout", SqlDbType.Int).Value = LockTimeoutMs;
                cmd.Parameters.Add("@year", SqlDbType.Int).Value = year;

                object? scalar = await cmd.ExecuteScalarAsync();
                if (scalar == null)
                {
                    return Common.Results.Result<string>.Fail(
                        "تعذر إسناد الرقم التجاري — صف العدّاد غير مقروء بعد القفل");
                }
                seq = (long)scalar;
            }

            // -1 = فشل القفل (مهلة/تداخل ميت) — لا رقم، والعدّاد لم يُلمس.
            if (seq < 0)
            {
                return Common.Results.Result<string>.Fail(
                    "ازدحام في إسناد الرقم التجاري — حاول مرة أخرى بعد لحظات.");
            }

            // seq يُنسَّق إلى 6 خانات ولا يُبتَر قط (BIGINT — أرقام تجارية
            // فوق 999999 تبقى صحيحة بلا حشو إضافي).
            return Common.Results.Result<string>.Ok(
                $"INV-{year.ToString(CultureInfo.InvariantCulture)}-" +
                seq.ToString("000000", CultureInfo.InvariantCulture));
        }
    }
}
