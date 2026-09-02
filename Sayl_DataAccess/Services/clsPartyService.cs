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



         public async Task<Result<int>> AddNewPartyWithRoleAsync(DtoParty dtoParty)
         {
             if (dtoParty.PartyType != 'C' && dtoParty.PartyType != 'S' && dtoParty.PartyType != 'B')
                 return Result<int>.Fail("نوع الطرف غير صالح.");

             if (string.IsNullOrWhiteSpace(dtoParty.Name))
                 return Result<int>.Fail("الاسم مطلوب.");

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

         // تحديث طرف (القيم المرسلة فقط)
         public async Task<Result> UpdatePartyAsync(DtoParty dtoParty)
         {
             if (dtoParty.PartyId <= 0)
                 return Result.Fail("معرف الطرف غير صالح.");

             Party? party = await _dbContext.Parties.FindAsync(dtoParty.PartyId);
             if (party == null)
                 return Result.Fail("الطرف غير موجود.");

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

             party.UpdatedAt = DateTime.Now;
             await _dbContext.SaveChangesAsync();
             return Result.Ok("تم تحديث الطرف بنجاح.");
         }

         // تعطيل طرف (حذف ناعم)
         public async Task<Result> DeactivatePartyAsync(int id)
         {
             Party? party = await _dbContext.Parties.FindAsync(id);
             if (party == null)
                 return Result.Fail("الطرف غير موجود.");

             party.IsActive = false;
             await _dbContext.SaveChangesAsync();
             return Result.Ok("تم تعطيل الطرف بنجاح.");
         }

         // إعادة تفعيل طرف
         public async Task<Result> ActivatePartyAsync(int id)
         {
             Party? party = await _dbContext.Parties.FindAsync(id);
             if (party == null)
                 return Result.Fail("الطرف غير موجود.");

             party.IsActive = true;
             await _dbContext.SaveChangesAsync();
             return Result.Ok("تم إعادة تفعيل الطرف بنجاح.");
         }
        
    }
}
