using Common.DTOs;
using Common.Exceptions;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
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
             // تتطلب محاسبًا؛ الإضافة السريعة من POS مسموحة للعميل النقدي فقط.
             if (!fromPosQuickAdd || dtoParty.PartyType != 'C')
             {
                 int actorRole = await GetUserRoleValueAsync(actorUserId ?? 0);
                 if (!clsPermissions.HasAtLeast(actorRole, clsPermissions.Accountant))
                     return Result<int>.Fail("إضافة أطراف (عملاء/موردين) تتطلب صلاحية محاسب أو أعلى.");
             }

             using var transaction = await _dbContext.Database.BeginTransactionAsync();
             try
             {
                 Party newParty = new Party
                 {
                     Name = clsValidation.ValidateString(dtoParty.Name, "Name"),
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
             catch
             {
                 await transaction.RollbackAsync();
                 return Result<int>.Fail("فشلت عملية الإضافة. الرجاء المحاولة مرة أخرى.");
             }
         }

          // تحديث طرف (القيم المرسلة فقط) — VULN-26: بوابة محاسب فأعلى
          public async Task<Result> UpdatePartyAsync(DtoParty dtoParty, int? actorUserId = null)
          {
              if (dtoParty.PartyId <= 0)
                  return Result.Fail("معرف الطرف غير صالح.");

              Party? party = await _dbContext.Parties.FindAsync(dtoParty.PartyId);
              if (party == null)
                  return Result.Fail("الطرف غير موجود.");

              int actorRole = await GetUserRoleValueAsync(actorUserId ?? 0);
              if (!clsPermissions.HasAtLeast(actorRole, clsPermissions.Accountant))
                  return Result.Fail("تعديل بيانات الأطراف يتطلب صلاحية محاسب أو أعلى.");

             if (!string.IsNullOrWhiteSpace(dtoParty.Name))
                 party.Name = clsValidation.ValidateString(dtoParty.Name, "Name");

             // السماح بتغيير النوع إلى 'B' فقط إذا كان حاليًا ليس 'B' (اختياري)
             if (party.PartyType != 'B' && dtoParty.PartyType == 'B')
                 party.PartyType = dtoParty.PartyType;

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
             await _dbContext.SaveChangesAsync();
             return Result.Ok("تم تحديث الطرف بنجاح.");
         }

          // تعطيل طرف (حذف ناعم)
          // VULN-34: طرف له رصيد مستحق لا يُعطَّل — كان التعطيل يخفيه من
          // كل القوائم والمنتقيات فيستحيل سداد رصيده المتبقي من أي شاشة
          // (عملاء: زر التحصيل يختفي؛ موردون: «سداد رصيد» يختفي) والدَّين
          // يبقى في التقارير للأبد. المطلوب: سدّد ثم عطّل.
          // VULN-26: وبوابة محاسب فأعلى — التعطيل قرار إداري.
          public async Task<Result> DeactivatePartyAsync(int id, int? actorUserId = null)
          {
              Party? party = await _dbContext.Parties.FindAsync(id);
              if (party == null)
                  return Result.Fail("الطرف غير موجود.");

              int actorRole = await GetUserRoleValueAsync(actorUserId ?? 0);
              if (!clsPermissions.HasAtLeast(actorRole, clsPermissions.Accountant))
                  return Result.Fail("تعطيل الأطراف يتطلب صلاحية محاسب أو أعلى.");

              if (party.PartyType == 'C')
              {
                  var customer = await _dbContext.Customers.FindAsync(id);
                  if (customer != null && (customer.Balance ?? 0) > 0)
                      return Result.Fail($"لا يمكن تعطيل العميل '{party.Name}' — عليه رصيد مستحق ({customer.Balance:N2}). حصّل الرصيد أولًا.");
              }
              else if (party.PartyType == 'S')
              {
                  var supplier = await _dbContext.Suppliers.FindAsync(id);
                  if (supplier != null && (supplier.Balance ?? 0) > 0)
                      return Result.Fail($"لا يمكن تعطيل المورد '{party.Name}' — له رصيد مستحق ({supplier.Balance:N2}). سدّد الرصيد أولًا.");
              }

              party.IsActive = false;
              await _dbContext.SaveChangesAsync();
              return Result.Ok("تم تعطيل الطرف بنجاح.");
          }

          // إعادة تفعيل طرف — VULN-26: بوابة محاسب فأعلى (قرار إداري معاكس)
          public async Task<Result> ActivatePartyAsync(int id, int? actorUserId = null)
          {
              Party? party = await _dbContext.Parties.FindAsync(id);
              if (party == null)
                  return Result.Fail("الطرف غير موجود.");

              int actorRole = await GetUserRoleValueAsync(actorUserId ?? 0);
              if (!clsPermissions.HasAtLeast(actorRole, clsPermissions.Accountant))
                  return Result.Fail("إعادة تفعيل الأطراف تتطلب صلاحية محاسب أو أعلى.");

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
