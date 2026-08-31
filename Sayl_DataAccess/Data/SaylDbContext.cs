using System;
using System.Collections.Generic;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using Sayl.Core.Models;

namespace Sayl.Core.Data;

public partial class SaylDbContext : DbContext
{
    public SaylDbContext()
    {
    }

    public SaylDbContext(DbContextOptions<SaylDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Batch> Batches { get; set; }

    public virtual DbSet<CashMovement> CashMovements { get; set; }

    public virtual DbSet<Customer> Customers { get; set; }

    public virtual DbSet<Employee> Employees { get; set; }

    public virtual DbSet<Expense> Expenses { get; set; }

    public virtual DbSet<Inventory> Inventories { get; set; }

    public virtual DbSet<Invoice> Invoices { get; set; }

    public virtual DbSet<InvoiceItem> InvoiceItems { get; set; }

    public virtual DbSet<Party> Parties { get; set; }

    public virtual DbSet<Payment> Payments { get; set; }

    public virtual DbSet<PosSession> PosSessions { get; set; }

    public virtual DbSet<Product> Products { get; set; }

    public virtual DbSet<Role> Roles { get; set; }

    public virtual DbSet<Setting> Settings { get; set; }

    public virtual DbSet<StockMovement> StockMovements { get; set; }

    public virtual DbSet<Supplier> Suppliers { get; set; }

    public virtual DbSet<SystemLog> SystemLogs { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<Shift> Shifts { get; set; }

    public virtual DbSet<EmployeeShift> EmployeeShifts { get; set; }

    public virtual DbSet<OfficialHoliday> OfficialHolidays { get; set; }

    public virtual DbSet<PayrollRecord> PayrollRecords { get; set; }

//    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
//#warning To protect potentially sensitive information in your connection string, you should move it out of source code. You can avoid scaffolding the connection string by using the Name= syntax to read it from configuration - see https://go.microsoft.com/fwlink/?linkid=2131148. For more guidance on storing connection strings, see https://go.microsoft.com/fwlink/?LinkId=723263.
//        => optionsBuilder.UseSqlServer("Server=.;Database=SaylDB;User Id=sa;Password=sa123456;TrustServerCertificate=True");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CashMovement>(entity =>
        {
            entity.HasKey(e => e.MovementId).HasName("PK_CashMovements");

            entity.HasIndex(e => e.SessionId, "IX_CashMovements_SessionId");

            entity.HasIndex(e => e.UserId, "IX_CashMovements_UserId");

            entity.HasIndex(e => e.CreatedAt, "IX_CashMovements_CreatedAt");

            entity.Property(e => e.MovementType)
                .HasMaxLength(1)
                .IsUnicode(false)
                .IsFixedLength();
            entity.Property(e => e.Amount)
                .HasColumnType("decimal(10, 2)")
                .HasDefaultValue(0m);
            entity.Property(e => e.Reason).HasMaxLength(255);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");

            entity.HasOne(d => d.PosSession).WithMany()
                .HasForeignKey(d => d.SessionId)
                .HasConstraintName("FK_CashMovements_SessionId");

            entity.HasOne(d => d.User).WithMany()
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_CashMovements_UserId");
        });

        modelBuilder.Entity<Setting>(entity =>
        {
            entity.HasKey(e => e.SettingKey).HasName("PK_Settings");

            entity.Property(e => e.SettingKey).HasMaxLength(100);
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("(getdate())");
        });

        modelBuilder.Entity<Batch>(entity =>
        {
            entity.HasKey(e => e.BatchId).HasName("PK__Batches__5D55CE580F5FA9C8");

            entity.HasIndex(e => e.BatchNumber, "IX_Batches_BatchNumber");

            entity.HasIndex(e => e.ExpiryDate, "IX_Batches_ExpiryDate");

            entity.HasIndex(e => e.ProductId, "IX_Batches_ProductId");

            entity.HasIndex(e => new { e.ProductId, e.BatchNumber }, "UQ_Batches_Product_Batch").IsUnique();

            entity.Property(e => e.BatchNumber).HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");

            entity.HasOne(d => d.Product).WithMany(p => p.Batches)
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Batches_ProductId");
        });

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.HasKey(e => e.PartyId).HasName("PK__Customer__1640CD33426764F1");

            entity.HasIndex(e => e.Balance, "IX_Customers_Balance");

            entity.Property(e => e.PartyId).ValueGeneratedNever();
            entity.Property(e => e.Balance)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(10, 2)");
            entity.Property(e => e.CreditLimit)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(10, 2)");

            entity.HasOne(d => d.Party).WithOne(p => p.Customer)
                .HasForeignKey<Customer>(d => d.PartyId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Customers_PartyId");
        });

        modelBuilder.Entity<Employee>(entity =>
        {
            entity.HasKey(e => e.EmployeeId).HasName("PK__Employee__7AD04F113B189FFF");

            entity.HasIndex(e => e.Department, "IX_Employees_Department");

            entity.HasIndex(e => e.Status, "IX_Employees_Status");

            entity.Property(e => e.Address).HasMaxLength(400);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.Department).HasMaxLength(50);
            entity.Property(e => e.Email).HasMaxLength(150);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Phone).HasMaxLength(20);
            entity.Property(e => e.Position).HasMaxLength(50);
            entity.Property(e => e.Salary).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.SalaryType)
                .HasMaxLength(10)
                .HasConversion<string>(); // لا HasDefaultValue — تُسقط العمود من INSERT عند القيم المساوية للـ sentinel
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValue("active");
        });

        modelBuilder.Entity<Shift>(entity =>
        {
            entity.HasKey(e => e.ShiftId).HasName("PK_Shifts");

            entity.HasIndex(e => e.Name, "UQ_Shifts_Name").IsUnique();

            entity.Property(e => e.Name).HasMaxLength(50);
            entity.Property(e => e.Notes).HasMaxLength(255);
        });

        modelBuilder.Entity<EmployeeShift>(entity =>
        {
            entity.HasKey(e => e.EmployeeShiftId).HasName("PK_EmployeeShifts");

            entity.HasIndex(e => e.EmployeeId, "IX_EmployeeShifts_EmployeeId");

            entity.HasIndex(e => new { e.EmployeeId, e.ShiftId, e.EffectiveFrom }, "UQ_EmployeeShifts_Active").IsUnique();

            entity.HasOne(d => d.Employee).WithMany(p => p.EmployeeShifts)
                .HasForeignKey(d => d.EmployeeId)
                .HasConstraintName("FK_EmployeeShifts_EmployeeId");

            entity.HasOne(d => d.Shift).WithMany(p => p.EmployeeShifts)
                .HasForeignKey(d => d.ShiftId)
                .HasConstraintName("FK_EmployeeShifts_ShiftId");
        });

        modelBuilder.Entity<OfficialHoliday>(entity =>
        {
            entity.HasKey(e => e.HolidayId).HasName("PK_OfficialHolidays");

            entity.HasIndex(e => e.HolidayDate, "IX_OfficialHolidays_Date");

            entity.Property(e => e.Name).HasMaxLength(100);
        });

        modelBuilder.Entity<PayrollRecord>(entity =>
        {
            entity.HasKey(e => e.PayrollId).HasName("PK_PayrollRecords");

            entity.HasIndex(e => e.EmployeeId, "IX_PayrollRecords_EmployeeId");

            entity.HasIndex(e => e.Status, "IX_PayrollRecords_Status");

            entity.HasIndex(e => new { e.PeriodFrom, e.PeriodTo }, "IX_PayrollRecords_Period");

            entity.HasIndex(e => new { e.EmployeeId, e.PeriodFrom, e.PeriodTo }, "UQ_PayrollRecords_Employee_Period").IsUnique();

            entity.Property(e => e.Additions).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.Deductions).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.Gross).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.HolidayUnits).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.Net).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.Rate).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.SalaryType).HasMaxLength(10).HasConversion<string>();
            entity.Property(e => e.Status)
                .HasMaxLength(10)
                .HasDefaultValue(enPayrollStatus.pending)
                .HasConversion<string>();
            entity.Property(e => e.WorkedUnits).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.Notes).HasMaxLength(255);

            entity.HasOne(d => d.Employee).WithMany(p => p.PayrollRecords)
                .HasForeignKey(d => d.EmployeeId)
                .HasConstraintName("FK_PayrollRecords_EmployeeId");

            entity.HasOne(d => d.Expense).WithMany()
                .HasForeignKey(d => d.ExpenseId)
                .HasConstraintName("FK_PayrollRecords_ExpenseId");
        });

        modelBuilder.Entity<Expense>(entity =>
        {
            entity.HasKey(e => e.ExpenseId).HasName("PK__Expenses__1445CFD3B7ECBCEF");

            entity.HasIndex(e => e.ExpenseDate, "IX_Expenses_ExpenseDate");

            entity.HasIndex(e => e.UserId, "IX_Expenses_UserId");

            entity.Property(e => e.Amount).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.Category)
                .HasMaxLength(50)
                .IsUnicode(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.Description).HasMaxLength(255);

            entity.HasOne(d => d.User).WithMany(p => p.Expenses)
                .HasForeignKey(d => d.UserId)
                .HasConstraintName("FK_Expenses_UserId");
        });

        modelBuilder.Entity<Inventory>(entity =>
        {
            entity.HasKey(e => e.ProductId).HasName("PK__Inventor__B40CC6CD2BEFCB08");

            entity.ToTable("Inventory");

            entity.HasIndex(e => e.QuantityAvailable, "IX_Inventory_QuantityAvailable");

            entity.Property(e => e.ProductId).ValueGeneratedNever();
            entity.Property(e => e.QuantityAvailable).HasDefaultValue(0);
            entity.Property(e => e.QuantityReserved).HasDefaultValue(0);
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("(getdate())");

            entity.HasOne(d => d.Product).WithOne(p => p.Inventory)
                .HasForeignKey<Inventory>(d => d.ProductId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Inventory_ProductId");
        });

        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.HasKey(e => e.InvoiceId).HasName("PK__Invoices__D796AAB56D46C156");

            entity.HasIndex(e => e.OriginalInvoiceId, "IX_Invoices_OriginalInvoiceId");

            entity.HasIndex(e => new { e.PartyType, e.PartyId }, "IX_Invoices_PartyType_PartyId");

            entity.HasIndex(e => e.SessionId, "IX_Invoices_SessionId");

            entity.HasIndex(e => new { e.Type, e.Status }, "IX_Invoices_Type_Status");

            entity.HasIndex(e => e.UserId, "IX_Invoices_UserId");

            entity.HasIndex(e => e.CreatedAt, "IX_Invoices_CreatedAt");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.DiscountAmount).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.NetAmount).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.PartyType)
                .HasMaxLength(1)
                .IsUnicode(false)
                .IsFixedLength();
            entity.Property(e => e.PaymentStatus)
                .HasMaxLength(20)
                .HasDefaultValue(enPaymentStatus.unpaid)
                .HasConversion<string>();
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValue(enInvoicesStatus.completed)
                .HasConversion<string>();
                
            entity.Property(e => e.TaxAmount).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.Type).HasMaxLength(20).HasConversion<string>();

           
            entity.HasOne(d => d.Party).WithMany(p => p.Invoices)
                .HasForeignKey(d => d.PartyId)
                .HasConstraintName("FK_Invoices_PartyId");

            entity.HasOne(d => d.PosSession).WithMany(p => p.Invoices)
                .HasForeignKey(d => d.SessionId)
                .HasConstraintName("FK_Invoices_SessionId");

        });

        modelBuilder.Entity<InvoiceItem>(entity =>
        {
            entity.HasKey(e => e.InvoiceItemId).HasName("PK__InvoiceI__478FE09C163D578C");

            entity.HasIndex(e => e.BatchId, "IX_InvoiceItems_BatchId");

            entity.HasIndex(e => e.InvoiceId, "IX_InvoiceItems_InvoiceId");

            entity.HasIndex(e => e.ProductId, "IX_InvoiceItems_ProductId");

            entity.Property(e => e.ItemPrice).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.ItemCost).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.Quantity).HasDefaultValue(1);

            entity.HasOne(d => d.Batch).WithMany(p => p.InvoiceItems)
                .HasForeignKey(d => d.BatchId)
                .HasConstraintName("FK_InvoicesItems_BatchId");

            entity.HasOne(d => d.Invoice).WithMany(p => p.InvoiceItems)
                .HasForeignKey(d => d.InvoiceId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_InvoiceItems_InvoiceId");

            entity.HasOne(d => d.Product).WithMany(p => p.InvoiceItems)
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_InvoicesItems_ProductId");
        });

        modelBuilder.Entity<Party>(entity =>
        {
            entity.HasKey(e => e.PartyId).HasName("PK__Parties__1640CD3322CECA2D");

            entity.HasIndex(e => e.Email, "IX_Parties_Email");

            entity.HasIndex(e => e.Name, "IX_Parties_Name");

            entity.HasIndex(e => e.PartyType, "IX_Parties_PartyType");

            entity.HasIndex(e => e.Email, "parties_email_unique").IsUnique();

            entity.Property(e => e.AddressLine1).HasMaxLength(400);
            entity.Property(e => e.AddressLine2).HasMaxLength(400);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.Email).HasMaxLength(150);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Name).HasMaxLength(150);
            entity.Property(e => e.Notes).HasMaxLength(600);
            entity.Property(e => e.PartyType)
                .HasMaxLength(1)
                .IsUnicode(false)
                .IsFixedLength();
            entity.Property(e => e.Phone1).HasMaxLength(20);
            entity.Property(e => e.Phone2).HasMaxLength(20);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasKey(e => e.PaymentId).HasName("PK__Payments__9B556A3815C2D686");

            entity.HasIndex(e => e.InvoiceId, "IX_Payments_InvoiceId");

            entity.HasIndex(e => new { e.PartyType, e.PartyId }, "IX_Payments_PartyType_PartyId");

            entity.HasIndex(e => e.PaymentDate, "IX_Payments_PaymentDate");

            entity.Property(e => e.Amount).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.PartyType)
                .HasMaxLength(1)
                .IsUnicode(false)
                .IsFixedLength();
            entity.Property(e => e.PaymentMethod)
                .HasMaxLength(50)
                .IsUnicode(true);
            entity.Property(e => e.Type).HasMaxLength(10).HasConversion<string>();

            entity.HasOne(d => d.Invoice).WithMany(p => p.Payments)
                .HasForeignKey(d => d.InvoiceId)
                .HasConstraintName("FK_Payments_InvoiceId");

            entity.HasOne(d => d.Party).WithMany(p => p.Payments)
                .HasForeignKey(d => d.PartyId)
                .HasConstraintName("FK_Payments_PartyId");
        });

        modelBuilder.Entity<PosSession>(entity =>
        {
            entity.HasKey(e => e.SessionId).HasName("PK__POSSessi__C9F4929024B26B94");

            entity.ToTable("POSSessions");

            entity.HasIndex(e => e.Status, "IX_POSSessions_Status");

            entity.HasIndex(e => e.UserId, "IX_POSSessions_UserId");

            entity.Property(e => e.ClosingAmount).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.ExpectedAmount).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.OpeningAmount).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.StartedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.Status)
                .HasMaxLength(10)
                .HasDefaultValue(enPosSessionStatusType.open)
                .HasConversion<string>();

            entity.HasOne(d => d.User).WithMany(p => p.PosSessions)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_POSSessions_UserId");
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(e => e.ProductId).HasName("PK__Products__B40CC6CD78560467");

            entity.HasIndex(e => e.Barcode, "IX_Products_Barcode");

            entity.HasIndex(e => e.Type, "IX_Products_Type");

            entity.HasIndex(e => e.Barcode, "UQ_Products_Barcode").IsUnique();

            entity.Property(e => e.Barcode).HasMaxLength(50);
            entity.Property(e => e.BasePrice).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.CostPrice)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(10, 2)");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.ReorderLevel).HasDefaultValue(0);
            entity.Property(e => e.Type).HasMaxLength(20);
            entity.Property(e => e.Unit).HasMaxLength(20);
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(e => e.RoleId).HasName("PK__Roles__8AFACE1A39FFE028");

            entity.HasIndex(e => e.Name, "UQ_Roles_Name").IsUnique();

            entity.Property(e => e.RoleId).ValueGeneratedOnAdd();
            entity.Property(e => e.Name).HasMaxLength(100);
        });

        modelBuilder.Entity<StockMovement>(entity =>
        {
            entity.HasKey(e => e.MovementId).HasName("PK__StockMov__D182244673BFFF05");

            entity.HasIndex(e => e.BatchId, "IX_StockMovements_BatchId");

            entity.HasIndex(e => e.MovementType, "IX_StockMovements_MovementType");

            entity.HasIndex(e => e.ProductId, "IX_StockMovements_ProductId");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.MovementType).HasMaxLength(30).HasConversion<string>();
            entity.Property(e => e.SourceType)
                .HasMaxLength(50)
                .IsUnicode(false);

            entity.HasOne(d => d.Batch).WithMany(p => p.StockMovements)
                .HasForeignKey(d => d.BatchId)
                .HasConstraintName("FK_StockMovements_BatchId");

            entity.HasOne(d => d.Product).WithMany(p => p.StockMovements)
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_StockMovements_ProductId");
        });

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.HasKey(e => e.PartyId).HasName("PK__Supplier__1640CD332EDB0134");

            entity.HasIndex(e => e.Balance, "IX_Suppliers_Balance");

            entity.HasIndex(e => e.Category, "IX_Suppliers_Category");

            entity.Property(e => e.PartyId).ValueGeneratedNever();
            entity.Property(e => e.Balance)
                .HasDefaultValue(0m)
                .HasColumnType("decimal(10, 2)");
            entity.Property(e => e.Category).HasMaxLength(100);

            entity.HasOne(d => d.Party).WithOne(p => p.Supplier)
                .HasForeignKey<Supplier>(d => d.PartyId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Suppliers_PartyId");
        });

        modelBuilder.Entity<SystemLog>(entity =>
        {
            entity.HasKey(e => e.LogId).HasName("PK__SystemLo__5E548648143C7F46");

            entity.HasIndex(e => e.UserId, "IX_SystemLogs_UserId");

            entity.Property(e => e.Action).HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())", "DF_SystemLogs_CreatedAt");

            entity.HasOne(d => d.User).WithMany(p => p.SystemLogs)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_SystemLogs_UserId");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.UserId).HasName("PK__Users__1788CC4C55AB6BE6");

            entity.HasIndex(e => e.RoleId, "IX_Users_RoleId");

            entity.HasIndex(e => e.Username, "UQ_Users_Username").IsUnique();

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.PasswordHash).HasMaxLength(255);
            entity.Property(e => e.Username).HasMaxLength(50);
            entity.Property(e => e.IsActive).HasDefaultValue(true);

        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
