/*
===========================================================
SaylDB Upgrade 03 — Employees, Shifts, Holidays, Payroll
- Employees       : + SalaryType (hourly/daily/monthly), existing rows default to 'monthly'
- Users           : UNIQUE(EmployeeId) — one account per employee
- Shifts          : shift definitions (schedule info, NOT attendance)
- EmployeeShifts  : per-employee shift assignment per weekday
- OfficialHolidays: paid official holidays (daily payroll + annual recurring)
- PayrollRecords  : payroll per employee per period; UNIQUE(EmployeeId, PeriodFrom, PeriodTo)
                   paying a record creates a linked Expense (category 'رواتب')
Run once against SaylDB. Safe to re-run (existence checks).
===========================================================
*/

USE SaylDB;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

-----------------------------------------------------------
-- 1) Employees.SalaryType
-----------------------------------------------------------
IF COL_LENGTH('dbo.Employees', 'SalaryType') IS NULL
BEGIN
    ALTER TABLE dbo.Employees
        ADD SalaryType NVARCHAR(10) NOT NULL DEFAULT ('monthly');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Employees_SalaryType')
BEGIN
    ALTER TABLE dbo.Employees
        ADD CONSTRAINT CK_Employees_SalaryType CHECK (SalaryType IN ('hourly','daily','monthly'));
END
GO

-----------------------------------------------------------
-- 2) Users: one account per employee
-----------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'UQ_Users_EmployeeId' AND object_id = OBJECT_ID('dbo.Users')
)
BEGIN
    -- pre-check: link duplicates would break creation; report them first
    IF EXISTS (
        SELECT 1 FROM dbo.Users
        WHERE EmployeeId IS NOT NULL
        GROUP BY EmployeeId HAVING COUNT(*) > 1
    )
    BEGIN
        RAISERROR (N'Cannot create UQ_Users_EmployeeId: some employees are linked to more than one user. Fix duplicates first.', 16, 1);
        RETURN;
    END

    CREATE UNIQUE INDEX UQ_Users_EmployeeId ON dbo.Users(EmployeeId) WHERE EmployeeId IS NOT NULL;
END
GO

-----------------------------------------------------------
-- 3) Shifts
-----------------------------------------------------------
IF OBJECT_ID('dbo.Shifts', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Shifts (
        ShiftId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Shifts PRIMARY KEY,
        Name      NVARCHAR(50)  NOT NULL,
        StartTime TIME          NOT NULL,
        EndTime   TIME          NOT NULL,  -- End < Start = crosses midnight
        Notes     NVARCHAR(255) NULL,

        CONSTRAINT UQ_Shifts_Name UNIQUE (Name),
        CONSTRAINT CK_Shifts_Times CHECK (StartTime <> EndTime)
    );
END
GO

-----------------------------------------------------------
-- 4) EmployeeShifts
-----------------------------------------------------------
IF OBJECT_ID('dbo.EmployeeShifts', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.EmployeeShifts (
        EmployeeShiftId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmployeeShifts PRIMARY KEY,
        EmployeeId INT NOT NULL,
        ShiftId    INT NOT NULL,
        DaysOfWeek TINYINT NOT NULL,        -- bitmask 1..127 (bit0=Sunday .. bit6=Saturday)
        EffectiveFrom DATE NOT NULL,
        EffectiveTo   DATE NULL,            -- NULL = open-ended

        CONSTRAINT FK_EmployeeShifts_EmployeeId FOREIGN KEY (EmployeeId) REFERENCES dbo.Employees(EmployeeId),
        CONSTRAINT FK_EmployeeShifts_ShiftId    FOREIGN KEY (ShiftId)    REFERENCES dbo.Shifts(ShiftId),
        CONSTRAINT CK_EmployeeShifts_Days CHECK (DaysOfWeek BETWEEN 1 AND 127),
        CONSTRAINT UQ_EmployeeShifts_Active UNIQUE (EmployeeId, ShiftId, EffectiveFrom)
    );
    CREATE INDEX IX_EmployeeShifts_EmployeeId ON dbo.EmployeeShifts(EmployeeId);
END
GO

-----------------------------------------------------------
-- 5) OfficialHolidays
-----------------------------------------------------------
IF OBJECT_ID('dbo.OfficialHolidays', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.OfficialHolidays (
        HolidayId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OfficialHolidays PRIMARY KEY,
        Name        NVARCHAR(100) NOT NULL,
        HolidayDate DATE NOT NULL,
        DurationDays INT NOT NULL DEFAULT (1),
        IsAnnual    BIT NOT NULL DEFAULT (0),

        CONSTRAINT CK_OfficialHolidays_Duration CHECK (DurationDays >= 1)
    );
    CREATE INDEX IX_OfficialHolidays_Date ON dbo.OfficialHolidays(HolidayDate);
END
GO

-----------------------------------------------------------
-- 6) PayrollRecords
-----------------------------------------------------------
IF OBJECT_ID('dbo.PayrollRecords', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PayrollRecords (
        PayrollId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PayrollRecords PRIMARY KEY,
        EmployeeId  INT NOT NULL,
        PeriodFrom  DATE NOT NULL,
        PeriodTo    DATE NOT NULL,
        SalaryType  NVARCHAR(10) NOT NULL,
        WorkedUnits DECIMAL(10,2) NOT NULL DEFAULT (0),
        HolidayUnits DECIMAL(10,2) NOT NULL DEFAULT (0),
        Rate        DECIMAL(10,2) NOT NULL DEFAULT (0),
        Gross       DECIMAL(10,2) NOT NULL DEFAULT (0),
        Deductions  DECIMAL(10,2) NOT NULL DEFAULT (0),
        Additions   DECIMAL(10,2) NOT NULL DEFAULT (0),
        Net         DECIMAL(10,2) NOT NULL DEFAULT (0),
        Status      NVARCHAR(10) NOT NULL DEFAULT ('pending'),
        PaidAt      DATETIME2 NULL,
        ExpenseId   INT NULL,
        Notes       NVARCHAR(255) NULL,
        CreatedAt   DATETIME2 DEFAULT (GETDATE()),

        CONSTRAINT FK_PayrollRecords_EmployeeId FOREIGN KEY (EmployeeId) REFERENCES dbo.Employees(EmployeeId),
        CONSTRAINT FK_PayrollRecords_ExpenseId  FOREIGN KEY (ExpenseId)  REFERENCES dbo.Expenses(ExpenseId),
        CONSTRAINT CK_PayrollRecords_Period CHECK (PeriodTo >= PeriodFrom),
        CONSTRAINT CK_PayrollRecords_Status CHECK (Status IN ('pending','paid')),
        CONSTRAINT CK_PayrollRecords_Net CHECK (Net >= 0),
        CONSTRAINT UQ_PayrollRecords_Employee_Period UNIQUE (EmployeeId, PeriodFrom, PeriodTo)
    );
    CREATE INDEX IX_PayrollRecords_EmployeeId ON dbo.PayrollRecords(EmployeeId);
    CREATE INDEX IX_PayrollRecords_Status ON dbo.PayrollRecords(Status);
    CREATE INDEX IX_PayrollRecords_Period ON dbo.PayrollRecords(PeriodFrom, PeriodTo);
END
GO

PRINT 'Upgrade 03 applied successfully.';
GO
