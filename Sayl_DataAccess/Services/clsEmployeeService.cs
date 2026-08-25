using Common.DTOs;
using Common.Exceptions;
using Common.Results;
using Common.Utils;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Data;
using Sayl.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;
namespace Sayl.Core.Services
{
    public class clsEmployeeService
    {
        private readonly SaylDbContext _dbContext;

        public clsEmployeeService(SaylDbContext dbContext)
        {
            _dbContext = dbContext;
        }


        public async Task<List<Employee>> GetEmployeesListAsync()
        {
            List<Employee> employees = await _dbContext.Employees.ToListAsync();

            return employees;
        }

        public async Task<Employee?> GetEmployeeByIdAsync(int id)
        {
            Employee? employee = await _dbContext.Employees.FindAsync(id);

            return employee;
        }

        public async Task<Result> AddNewEmployeeAsync(DtoEmployee dtoEmployee)
        {
            if (string.IsNullOrWhiteSpace(dtoEmployee.Name))
                return Result.Fail("الاسم مطلوب.");

            if (string.IsNullOrWhiteSpace(dtoEmployee.Position))
                return Result.Fail("المسمى الوظيفي مطلوب.");

            string[] validStatuses = { "active", "on_leave", "resigned" };
            if (string.IsNullOrWhiteSpace(dtoEmployee.Status) || !validStatuses.Contains(dtoEmployee.Status.ToLower()))
                return Result.Fail("حالة الموظف غير صالحة.");


            var employee = new Employee
            {
                Name = clsValidation.ValidateString(dtoEmployee.Name, "Name"),
                Phone = string.IsNullOrWhiteSpace(dtoEmployee.Phone) ? null : dtoEmployee.Phone,
                Email = string.IsNullOrWhiteSpace(dtoEmployee.Email) ? null : dtoEmployee.Email,
                Salary = dtoEmployee.Salary,
                HireDate = dtoEmployee.HireDate.HasValue ? dtoEmployee.HireDate.Value : null,
                Address = string.IsNullOrWhiteSpace(dtoEmployee.Address) ? null : dtoEmployee.Address,
                Status = dtoEmployee.Status.ToLower(),
                Position = clsValidation.ValidateString(dtoEmployee.Position, "Position"),
                Department = string.IsNullOrWhiteSpace(dtoEmployee.Department) ? null : dtoEmployee.Department
            };

            _dbContext.Employees.Add(employee);
            bool saved = (await _dbContext.SaveChangesAsync()) > 0;
            return saved ? Result<int>.Ok(employee.EmployeeId,"تمت إضافة الموظف بنجاح.") : Result.Fail("فشلت الإضافة.");
        }

        public async Task<Result> UpdateEmployeeAsync(DtoEmployee dtoEmployee)
        {
            if (dtoEmployee.EmployeeId <= 0)
                return Result.Fail("معرف الموظف غير صالح.");

            Employee? employee = await _dbContext.Employees.FindAsync(dtoEmployee.EmployeeId);
            if (employee == null)
                return Result.Fail("الموظف غير موجود.");

            if (!string.IsNullOrWhiteSpace(dtoEmployee.Name))
                employee.Name = clsValidation.ValidateString(dtoEmployee.Name, "Name");

            if (!string.IsNullOrWhiteSpace(dtoEmployee.Phone))
                employee.Phone = dtoEmployee.Phone;

            if (!string.IsNullOrWhiteSpace(dtoEmployee.Email))
                employee.Email = clsValidation.ValidateEmail(dtoEmployee.Email);

            if (dtoEmployee.Salary.HasValue)
                employee.Salary = dtoEmployee.Salary.Value;

            if (dtoEmployee.HireDate.HasValue)
                employee.HireDate = dtoEmployee.HireDate.Value;

            if (!string.IsNullOrWhiteSpace(dtoEmployee.Address))
                employee.Address = dtoEmployee.Address;

            if (!string.IsNullOrWhiteSpace(dtoEmployee.Status))
            {
                string[] validStatuses = { "active", "on_leave", "resigned" };
                if (validStatuses.Contains(dtoEmployee.Status.ToLower()))
                    employee.Status = dtoEmployee.Status.ToLower();
                else
                    return Result.Fail("حالة الموظف غير صالحة.");
            }

            if (!string.IsNullOrWhiteSpace(dtoEmployee.Position))
                employee.Position = dtoEmployee.Position;

            if (!string.IsNullOrWhiteSpace(dtoEmployee.Department))
                employee.Department = dtoEmployee.Department;

            await _dbContext.SaveChangesAsync();
            return Result.Ok("تم تحديث بيانات الموظف بنجاح.");
        }

        // تعطيل / تفعيل موظف (يمكن دمجه مع الحذف الناعم)
        public async Task<Result> SetEmployeeStatusAsync(int employeeId, string status)
        {
            string[] validStatuses = { "active", "on_leave", "resigned" };
            if (!validStatuses.Contains(status.ToLower()))
                return Result.Fail("حالة غير صالحة.");

            Employee? employee = await _dbContext.Employees.FindAsync(employeeId);
            if (employee == null)
                return Result.Fail("الموظف غير موجود.");

            employee.Status = status.ToLower();
            await _dbContext.SaveChangesAsync();
            return Result.Ok($"تم تغيير حالة الموظف إلى {status}.");
        }
    }
    
}
