using Common.DTOs;
using Common.Interfaces;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sayl.Core.Models;
using Sayl.Core.Data;
namespace Sayl.Services
{
    public class clsCustomersServices
    {


        private readonly SaylDbContext _dbContext;

        public clsCustomersServices(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        private static IQueryable<DtoCustomer> CustomerQueryProjection(IIncludableQueryable<Models.Party, Models.Customer> query)
        {

            IQueryable<DtoCustomer> customerQuery = query.Select(c => new DtoCustomer
            {
                Id = c.PartyId,
                PartyType = c.PartyType,
                Name = c.Name,
                Phone1 = c.Phone1,
                AddressLine1 = c.AddressLine1,
                AddressLine2 = c.AddressLine2,
                Balance = c.Customer.Balance,
                CreatedAt = c.CreatedAt,
                Email = c.Email,
                IsActive = c.IsActive,
                Notes = c.Notes,
                Phone2 = c.Phone2,
                UpdatedAt = c.UpdatedAt,
                CreditLimit = c.Customer.CreditLimit
            });
            return customerQuery;
        }

        static void MapThroughCustomer(Party party, DtoCustomer dto)
        {
            party.Notes = dto.Notes;
            party.Email = dto.Email;
            party.AddressLine1 = dto.AddressLine1;
            party.Phone1 = dto.Phone1;
            party.AddressLine2 = dto.AddressLine2;
            party.Phone2 = dto.Phone2;
            party.Name = dto.Name;
        }
        public async Task<List<DtoCustomer>> GetCustomersAsync(string search)
        {
            var query = _dbContext.Parties.Where(c => string.IsNullOrWhiteSpace(search) || c.Name.Contains(search) || c.Email.Contains(search)).Include(s => s.Customer);

            List<DtoCustomer> customers = await CustomerQueryProjection(query).ToListAsync();

            return customers;
        }
        private readonly ICustomerData _data;

        // استقبال ICustomerData عبر المُنشئ (حقن التبعية)
        public clsCustomersServices(ICustomerData data)
        {
            _data = data;
        }

        public async Task<List<DtoCustomer>> GetCustomersAsync(string search = "")
        {
            // يمكنك إضافة منطق إضافي هنا (فلترة، تحقق، حساب خصم...)
            return await _data.GetCustomersAsync(search);
        }
    }
}
