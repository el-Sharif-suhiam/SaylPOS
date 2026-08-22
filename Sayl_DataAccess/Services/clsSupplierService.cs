using Common.DTOs;
using Common.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Sayl.Core.Data;
using Sayl.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Sayl.Core.Services
{
    public class clsSupplierService
    {
        private readonly SaylDbContext _dbContext;

        public clsSupplierService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<List<Supplier>> GetSupplierListAsync(string search = "")
        {

            var query = _dbContext.Suppliers.Include(c => c.Party).Where(c => string.IsNullOrWhiteSpace(search) || c.Party.Name.Contains(search) || c.Party.Email.Contains(search));
            List<Supplier> suppliers = await query.ToListAsync();

            return suppliers;
        }

        public async Task<Supplier?> GetSupplierByIdAsync(int id)
        {
            var query = _dbContext.Suppliers.Include(s => s.Party);

            //IQueryable<DtoCustomer> customersQuery = CustomerQueryProjection(query);

            Supplier? supplier = await query.SingleOrDefaultAsync(c => c.PartyId == id);

            return supplier;
        }
      
    }
}
