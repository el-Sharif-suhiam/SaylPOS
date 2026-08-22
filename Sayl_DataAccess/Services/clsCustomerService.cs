using Common.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Sayl.Core.Models;
using Common.Interfaces;
using Common.Exceptions;
using Sayl.Core.Data;

namespace Sayl.Core.Services
{
    public class clsCustomerService
    {

        private readonly SaylDbContext _dbContext;

        public clsCustomerService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        private static IQueryable<DtoCustomer> CustomerQueryProjection(IIncludableQueryable<Models.Party,Models.Customer> query)
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
        public async Task<List<Customer>> GetCustomersListAsync(string search = "")
        {

            var query = _dbContext.Customers.Include(c => c.Party).Where(c => string.IsNullOrWhiteSpace(search) || c.Party.Name.Contains(search) || c.Party.Email.Contains(search));
            List<Customer> customers = await query.ToListAsync();

            return customers;
        }

        public async Task<Customer?> GetCustomerByIdAsync( int id)
        {
            var query = _dbContext.Customers.Include(s => s.Party);

            //IQueryable<DtoCustomer> customersQuery = CustomerQueryProjection(query);

            Customer? customer = await query.SingleOrDefaultAsync(c => c.PartyId == id);
                
            return customer;
        }

        public async Task<bool> UpdateCustomerCreditLimitAsync(int cutomerId, decimal creditLimit)
        {
            if (creditLimit < 0)
                throw new ConflictException("The credit limit should be 0 at leaste");

            Customer customer = await _dbContext.Customers.SingleOrDefaultAsync(c =>  c.PartyId == cutomerId);

            if (customer == null)
                throw new NotFoundException("this customer is not found");
            
            customer.CreditLimit = creditLimit;

            return (await _dbContext.SaveChangesAsync()) > 0;

        }


        //public  async Task<bool> UpdateCustomerAsync( DtoCustomer dtoCustomer)
        //{
        //    Customer customer = await _dbContext.Customers.Include(s => s.Party).SingleOrDefaultAsync(c => c.PartyId == dtoCustomer.Id);

        //    if (customer == null)
        //        throw new NotFoundException("this customer is not found");

        //    customer.
        //    //customer.Email = dtoCustomer.Email;
        //    //customer.AddressLine1 = dtoCustomer.AddressLine1;
        //    //customer.AddressLine2 = dtoCustomer.AddressLine2;
        //    MapThroughCustomer(customer, dtoCustomer);

        //   return (await _dbContext.SaveChangesAsync())> 0;

        //}

    }
}
