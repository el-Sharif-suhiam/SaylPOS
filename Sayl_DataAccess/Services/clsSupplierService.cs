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

        public async Task<List<Supplier>> GetSupplierListAsync(string search = "", bool includeInactive = false)
        {
            var query = _dbContext.Suppliers.Include(c => c.Party)
                .Where(c => string.IsNullOrWhiteSpace(search) || c.Party.Name.Contains(search) || c.Party.Email.Contains(search));

            // الافتراضي: الموردون النشطون فقط — العرض الكامل عند الطلب من الواجهة
            if (!includeInactive)
                query = query.Where(c => c.Party.IsActive);

            query = query.OrderBy(c => c.Party.Name);

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
