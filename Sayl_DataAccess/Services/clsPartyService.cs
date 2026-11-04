using Common.DTOs;
using Common.Exceptions;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using System.ComponentModel.DataAnnotations;
namespace Sayl.Core.Services
{
    public class clsPartyService
    {
        private readonly SaylDbContext _dbContext;

        public clsPartyService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }



         // VULN-26: CRUD الأطراف كان بلا أي بوابة خدمية — أي دور يصل للخدمة
         // يضيف/يعدل عملاء وموردين. البوابة: محاسب فأعلى (إدارة الأطراف
         // قرار مكتب خلفي؛ شاشة POS تضيف «عميل نقدي جديد» من منتقي العملاء
         // — يستثنى صراحةً أدناه لأن إنشاء عميل سريع أثناء البيع سلوك شرعي).
         public async Task<Result<int>> AddNewPartyWithRoleAsync(DtoParty dtoParty, int? actorUserId = null, bool fromPosQuickAdd = false)
         {
             if (dtoParty.PartyType != 'C' && dtoParty.PartyType != 'S' && dtoParty.PartyType != 'B')
                 return Result<int>.Fail("نوع الطرف غير صالح.");

             if (string.IsNullOrWhiteSpace(dtoParty.Name))
                 return Result<int>.Fail("الاسم مطلوب.");

          // بوابة الخدمة: الإضافة الكاملة (كل الأنواع) والـ S/B من أي مكان
          // تتطلب شاشة العملاء (عملاء) أو الموردين (موردين) حسب النوع؛
          // الإضافة السريعة من POS مسموحة للعميل النقدي فقط (قراءة مشتركة).
          if (!fromPosQuickAdd || dtoParty.PartyType != 'C')
          {
              int actorRole = await GetUserRoleValueAsync(actorUserId ?? 0);
              int required = dtoParty.PartyType == 'S' ? AppPermissions.Suppliers : AppPermissions.Customers;
              if (!AppPermissions.Has(actorRole, required))
                  return Result<int>.Fail("إضافة هذا النوع من الأطراف تتطلب صلاحية شاشته المخصصة.");
          }

             // فحص الاسم قبل فتح المعاملة: أخطاء الشكل (فارغ/طول) تعود
             // برسالتها الحقيقية لا برسالة DB عامة — المصدر: ValidationException
             try
             {
                 dtoParty.Name = clsValidation.ValidateString(dtoParty.Name, "Name");
             }
             catch (ValidationException vex)
             {
                 return Result<int>.Fail(vex.Message);
             }

              using var transaction = await _dbContext.Database.BeginTransactionAsync();
              try
             {
                  Party newParty = new Party
                  {
                      Name = dtoParty.Name, // مُتحقق منه قبل فتح المعاملة أعلاه
                     PartyType = dtoParty.PartyType,
                     IsActive = dtoParty.IsActive,
                     Email = string.IsNullOrWhiteSpace(dtoParty.Email) ? null : dtoParty.Email,
                     Phone1 = string.IsNullOrWhiteSpace(dtoParty.Phone1) ? null : dtoParty.Phone1,
                     Phone2 = string.IsNullOrWhiteSpace(dtoParty.Phone2) ? null : dtoParty.Phone2,
                     AddressLine1 = string.IsNullOrWhiteSpace(dtoParty.AddressLine1) ? null : dtoParty.AddressLine1,
                     AddressLine2 = string.IsNullOrWhiteSpace(dtoParty.AddressLine2) ? null : dtoParty.AddressLine2,
                     Notes = string.IsNullOrWhiteSpace(dtoParty.Notes) ? null : dtoParty.Notes
                 };

                 _dbContext.Parties.Add(newParty);
                 await _dbContext.SaveChangesAsync();

                 if (dtoParty.PartyType == 'C' || dtoParty.PartyType == 'B')
                     _dbContext.Customers.Add(new Customer { PartyId = newParty.PartyId, Balance = 0, CreditLimit = 0 });

                 if (dtoParty.PartyType == 'S' || dtoParty.PartyType == 'B')
                     _dbContext.Suppliers.Add(new Supplier { PartyId = newParty.PartyId, Balance = 0, Category = null });

                 await _dbContext.SaveChangesAsync();
                 await transaction.CommitAsync();
                 return Result<int>.Ok(newParty.PartyId, "تمت إضافة الطرف بنجاح.");
             }
             catch (ValidationException vex)
             {
                 await transaction.RollbackAsync();
                 return Result<int>.Fail(vex.Message);
             }
             catch (DbUpdateException dbEx) when (dbEx.InnerException is Microsoft.Data.SqlClient.SqlException sql &&
                                                  sql.Number == 2601 &&
                                                  sql.Message.Contains("parties_email_unique"))
             {
                 // Upgrade_10: البريد الفريد المُرشّح — تعارض بريد فعلي (ليس NULL)
                 await transaction.RollbackAsync();
                 return Result<int>.Fail(string.IsNullOrWhiteSpace(dtoParty.Email)
                     ? "بريد الطرف مطلوب لإكمال الإضافة."
                     : $"البريد الإلكتروني «{dtoParty.Email}» مستخدم مسبقًا لطرف آخر.");
             }
             catch (Exception ex)
             {
                 await transaction.RollbackAsync();
                 return Result<int>.Fail($"فشلت عملية الإضافة: {ex.Message}");
             }
         }

        /// <summary>
        /// تحديث طرف (القيم المرسلة فقط) — VULN-26: بوابة محاسب فأعلى.
        ///
        /// الأطراف المشتركة ('B' عميل + مورد معًا): التحويل من 'C' أو 'S' إلى 'B'
        /// يُنشئ السجل الناقص (سجل مورد لعميل، أو سجل عميل لمورد) بنفس PartyId —
        /// فلا تُفقد أي سجلات ولا تاريخ معاملات (الفواتير والمدفوعات مربوطة
        /// بـPartyId الذي لا يتغير). التحويل idempotent: لو وُجد السجل مسبقًا
        /// لا يُكرر. النزول من 'B' إلى 'C'/'S' ممنوع — يخفي أحد السجلين من
        /// شاشته وتاريخه المالي من منتقيه.
        /// </summary>
        public async Task<Result> UpdatePartyAsync(DtoParty dtoParty, int? actorUserId = null)
        {
            if (dtoParty.PartyId <= 0)
                return Result.Fail("معرف الطرف غير صالح.");

            Party? party = await _dbContext.Parties.FindAsync(dtoParty.PartyId);
            if (party == null)
                return Result.Fail("الطرف غير موجود.");

            int actorRole = await GetUserRoleValueAsync(actorUserId ?? 0);

            // ══ تحويل النوع: قواعد البوابة والسجلات ══
            bool convertingToCombined = false;
            if (party.PartyType == 'B')
            {
                if (dtoParty.PartyType != 'B')
                    return Result.Fail("لا يمكن تحويل طرف مشترك إلى عميل أو مورد وحيد.");
            }
            else if (dtoParty.PartyType == 'B')
            {
                // التحويل من عميل ← مشترك يُنشئ سجل مورد: صلاحية الموردين مطلوبة أيضًا
                if (party.PartyType == 'C' && !AppPermissions.Has(actorRole, AppPermissions.Suppliers))
                    return Result.Fail("تحويل عميل إلى طرف مشترك يتطلب صلاحية الموردين أيضًا.");

                // التحويل من مورد ← مشترك يُنشئ سجل عميل: صلاحية العملاء مطلوبة أيضًا
                if (party.PartyType == 'S' && !AppPermissions.Has(actorRole, AppPermissions.Customers))
                    return Result.Fail("تحويل مورد إلى طرف مشترك يتطلب صلاحية العملاء أيضًا.");

                convertingToCombined = true;
            }
            else
            {
                int required = party.PartyType == 'S' ? AppPermissions.Suppliers : AppPermissions.Customers;
                if (!AppPermissions.Has(actorRole, required))
                    return Result.Fail("تعديل بيانات هذا الطرف يتطلب صلاحية شاشته المخصصة.");
            }

            if (!string.IsNullOrWhiteSpace(dtoParty.Name))
                party.Name = clsValidation.ValidateString(dtoParty.Name, "Name");

            party.PartyType = convertingToCombined ? 'B' : party.PartyType;

            if (!string.IsNullOrWhiteSpace(dtoParty.Email))
                party.Email = clsValidation.ValidateEmail(dtoParty.Email);

            if (!string.IsNullOrWhiteSpace(dtoParty.Phone1))
                party.Phone1 = dtoParty.Phone1;

            if (!string.IsNullOrWhiteSpace(dtoParty.Phone2))
                party.Phone2 = dtoParty.Phone2;

            if (!string.IsNullOrWhiteSpace(dtoParty.AddressLine1))
                party.AddressLine1 = dtoParty.AddressLine1;

            if (!string.IsNullOrWhiteSpace(dtoParty.AddressLine2))
                party.AddressLine2 = dtoParty.AddressLine2;

            if (!string.IsNullOrWhiteSpace(dtoParty.Notes))
                party.Notes = dtoParty.Notes;

            party.UpdatedAt = DateTime.UtcNow;

            // ══ إنشاء السجل الناقص عند التحويل إلى 'B' ══
            // نفس PartyId في الجدولين — السجلات المالية القائمة (فواتير، مدفوعات،
            // أرصدة) تبقى سليمة. الفحص المسبق يمنع التكرار.
            if (convertingToCombined)
            {
                bool hasSupplier = await _dbContext.Suppliers.AnyAsync(s => s.PartyId == party.PartyId);
                if (!hasSupplier)
                    _dbContext.Suppliers.Add(new Supplier { PartyId = party.PartyId, Balance = 0, Category = null });

                bool hasCustomer = await _dbContext.Customers.AnyAsync(c => c.PartyId == party.PartyId);
                if (!hasCustomer)
                    _dbContext.Customers.Add(new Customer { PartyId = party.PartyId, Balance = 0, CreditLimit = 0 });
            }

            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تحديث الطرف بنجاح.");
        }

        /// <summary>
        /// تعطيل طرف (حذف ناعم) — VULN-34: طرف له رصيد مستحق لا يُعطَّل — كان
        /// التعطيل يخفيه من كل القوائم والمنتقيات فيستحيل سداد رصيده المتبقي
        /// من أي شاشة (عملاء: زر التحصيل يختفي؛ موردون: «سداد رصيد» يختفي)
        /// والدين يبقى في التقارير للأبد. المطلوب: سدّد ثم عطّل.
        /// الأطراف المشتركة ('B'): يُفحص رصيد العميل ورصيد المورد معًا —
        /// أي رصيد مستحق على أي من السجلين يمنع التعطيل.
        /// VULN-26: وبوابة محاسب فأعلى — التعطيل قرار إداري.
        /// </summary>
        public async Task<Result> DeactivatePartyAsync(int id, int? actorUserId = null)
        {
            Party? party = await _dbContext.Parties.FindAsync(id);
            if (party == null)
                return Result.Fail("الطرف غير موجود.");

            int actorRole = await GetUserRoleValueAsync(actorUserId ?? 0);
            int requiredDeact = party.PartyType == 'S' ? AppPermissions.Suppliers : AppPermissions.Customers;
            if (!AppPermissions.Has(actorRole, requiredDeact))
                return Result.Fail("تعطيل الأطراف يتطلب صلاحية شاشته المخصصة.");

            // الفحص بالسجلات الفعلية لا بالنوع فقط: الطرف المشترك ('B') له
            // سجلا عميل ومورد كلاهما يحمل رصيدًا مستقلًا يجب فحصه.
            var customer = await _dbContext.Customers.FindAsync(id);
            if (customer != null && (customer.Balance ?? 0) > 0)
                return Result.Fail($"لا يمكن تعطيل العميل '{party.Name}' — عليه رصيد مستحق ({customer.Balance:N2}). حصّل الرصيد أولًا.");

            var supplier = await _dbContext.Suppliers.FindAsync(id);
            if (supplier != null && (supplier.Balance ?? 0) > 0)
                return Result.Fail($"لا يمكن تعطيل المورد '{party.Name}' — له رصيد مستحق ({supplier.Balance:N2}). سدّد الرصيد أولًا.");

            party.IsActive = false;
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تعطيل الطرف بنجاح.");
        }

        /// <summary>إعادة تفعيل طرف — VULN-26: بوابة محاسب فأعلى (قرار إداري معاكس).</summary>
        public async Task<Result> ActivatePartyAsync(int id, int? actorUserId = null)
        {
            Party? party = await _dbContext.Parties.FindAsync(id);
            if (party == null)
                return Result.Fail("الطرف غير موجود.");

            int actorRole = await GetUserRoleValueAsync(actorUserId ?? 0);
            int requiredAct = party.PartyType == 'S' ? AppPermissions.Suppliers : AppPermissions.Customers;
            if (!AppPermissions.Has(actorRole, requiredAct))
                return Result.Fail("إعادة تفعيل الأطراف تتطلب صلاحية شاشته المخصصة.");

            party.IsActive = true;
            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم إعادة تفعيل الطرف بنجاح.");
        }

        /// <summary>قيمة دور المستخدم (VULN-26 — بوابات الأطراف الخدمية).</summary>
        private async Task<int> GetUserRoleValueAsync(int userId)
        {
            if (userId <= 0)
                return 0;
            var user = await _dbContext.Users.Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.UserId == userId);
            return user?.Role.RoleValue ?? 0;
        }
    }
  }
