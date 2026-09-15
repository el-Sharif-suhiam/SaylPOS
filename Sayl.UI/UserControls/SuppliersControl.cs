using Common.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>قسم الموردين: عرض، بحث، إضافة/تعديل، تعطيل.</summary>
    internal class SuppliersControl : SectionListControl
    {
        private List<Supplier> _suppliers = new();

        protected override string Title => AppLang.T("Suppliers.Section.Title");

        // مفتاح «عرض المعطّلين» — الافتراضي: الموردون النشطون فقط
        protected override bool SupportsShowDeleted => true;

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var supplierService = scope.ServiceProvider.GetRequiredService<clsSupplierService>();
                var paged = await supplierService.GetSuppliersPagedAsync(
                    TxtSearch.Text?.Trim() ?? "", includeInactive: ShowInactive,
                    page: _currentPage, pageSize: _pageSize);
                if (!ApplyPaged(paged))
                    return;
                _suppliers = paged.Items;

                Grid.Columns.Clear();
                Grid.Columns.Add("colName", AppLang.T("Common.Label.Name"));
                Grid.Columns.Add("colPhone", AppLang.T("Common.Label.Phone"));
                Grid.Columns.Add("colCategory", AppLang.T("Suppliers.Label.Category"));
                Grid.Columns.Add("colBalance", AppLang.T("Suppliers.Label.Balance"));
                Grid.Columns.Add("colStatus", AppLang.T("Common.Label.Status"));
                Grid.Columns["colName"].FillWeight = 30;
                Grid.Columns["colPhone"].FillWeight = 20;
                Grid.Columns["colCategory"].FillWeight = 15;
                Grid.Columns["colBalance"].FillWeight = 15;
                Grid.Columns["colStatus"].FillWeight = 12;

                var colDetails = new DataGridViewButtonColumn
                {
                    Name = "colDetails",
                    HeaderText = "",
                    Text = AppLang.T("Common.Button.Details"),
                    UseColumnTextForButtonValue = true,
                    FlatStyle = FlatStyle.Flat,
                    FillWeight = 8
                };
                colDetails.DefaultCellStyle.ForeColor = AppTheme.Primary;
                colDetails.DefaultCellStyle.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
                Grid.Columns.Add(colDetails);

                Grid.Rows.Clear();
                foreach (var s in _suppliers.Where(s => s.Party != null))
                    Grid.Rows.Add(s.Party!.Name, s.Party.Phone1 ?? "-", s.Category ?? "-",
                        UiHelpers.Money(s.Balance ?? 0), s.Party.IsActive ? AppLang.T("Common.Status.Active") : AppLang.T("Common.Status.Inactive"));
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Suppliers.Msg.LoadFailed"));
            }
        }

        // 📖 أزرار القسم — وصفة AddToolbarButton (والحيلة البصرية
        // BringToFront) مشروحة في SectionListControl أعلى الملف.
        // أول Add هنا = أقصى اليمين بصريًا: إضافة ← تعديل ← سداد ← تعطيل
        // («إعادة تفعيل» زر أدمن شرطي — إضافة داخل if كما في SettingsControl).
        protected override void BuildToolbar()
        {
            AddToolbarButton(AppLang.T("Suppliers.Button.Add"), AppTheme.Primary, AppTheme.PrimaryDark, (_, _) => EditParty(null), 140);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Common.Button.Edit"), AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => EditSelected(), 110);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Suppliers.Button.PayBalance"), AppTheme.Success, Color.FromArgb(21, 128, 61), (_, _) => PaySupplierBalance(), 130);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Suppliers.Button.Deactivate"), AppTheme.Danger, Color.FromArgb(185, 28, 28), async (_, _) => await DeactivateAsync(), 110);

            if (SessionState.Can(Common.Utils.AppPermissions.Suppliers))
            {
                AddToolbarGap();
                AddToolbarButton(AppLang.T("Suppliers.Button.Activate"), AppTheme.Success, Color.FromArgb(21, 128, 61),
                    async (_, _) => await ActivateAsync(), 130);
            }

            Grid.CellClick += Grid_CellClick;
        }

        /// <summary>سداد على رصيد المورد بدون ربط بفاتورة (PaySupplierAsync).</summary>
        private void PaySupplierBalance()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            var visible = _suppliers.Where(s => s.Party != null).ToList();
            if (idx < 0 || idx >= visible.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Suppliers.Msg.PickSupplier"));
                return;
            }

            var supplier = visible[idx];
            decimal balance = supplier.Balance ?? 0;
            if (balance <= 0)
            {
                UiHelpers.ShowInfo(this, AppLang.T("Suppliers.Msg.NoDueBalance"));
                return;
            }

            using var form = SupplierPaymentForm.ForSupplierBalance(supplier.PartyId, supplier.Party!.Name, balance);
            if (form.ShowDialog(FindForm()) == DialogResult.OK)
                _ = LoadDataAsync();
        }

        private void Grid_CellClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _suppliers.Count) return;
            if (Grid.Columns[e.ColumnIndex].Name != "colDetails") return;

            var visible = _suppliers.Where(s => s.Party != null).ToList();
            if (e.RowIndex >= visible.Count) return;

            var supplier = visible[e.RowIndex];
            var party = supplier.Party!;
            using var form = new PartyDetailsForm(party,
                (AppLang.T("Suppliers.Details.Balance"), UiHelpers.Money(supplier.Balance ?? 0)),
                (AppLang.T("Suppliers.Details.Category"), supplier.Category ?? "-"));
            form.ShowDialog(FindForm());
        }

        private async Task ActivateAsync()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            var visible = _suppliers.Where(s => s.Party != null).ToList();
            if (idx < 0 || idx >= visible.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Suppliers.Msg.PickSupplier"));
                return;
            }

            var party = visible[idx].Party!;
            if (party.IsActive)
            {
                UiHelpers.ShowInfo(this, AppLang.T("Suppliers.Msg.AlreadyActive", party.Name));
                return;
            }

            if (!ConfirmationDialog.Show(this, AppLang.T("Suppliers.Title.Activate"),
                    AppLang.T("Suppliers.Msg.ActivateConfirm", party.Name), dangerStyle: false))
                return;

            Common.Results.Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var partyService = scope.ServiceProvider.GetRequiredService<clsPartyService>();
                result = await partyService.ActivatePartyAsync(party.PartyId, SessionState.CurrentUser?.UserId ?? 0); // VULN-26: بوابة خدمية
            }

            UiHelpers.ShowResult(this, result);
            if (result.IsSuccess)
                await LoadDataAsync();
        }

        private void EditSelected()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            var visible = _suppliers.Where(s => s.Party != null).ToList();
            if (idx < 0 || idx >= visible.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Suppliers.Msg.PickSupplier"));
                return;
            }
            EditParty(visible[idx].Party);
        }

        private void EditParty(Sayl.Core.Models.Party? existing)
        {
            DtoParty? dto = null;
            if (existing != null)
            {
                dto = new DtoParty
                {
                    PartyId = existing.PartyId,
                    PartyType = existing.PartyType,
                    Name = existing.Name,
                    Phone1 = existing.Phone1,
                    Phone2 = existing.Phone2,
                    Email = existing.Email,
                    AddressLine1 = existing.AddressLine1,
                    Notes = existing.Notes,
                    IsActive = existing.IsActive,
                    CreatedAt = existing.CreatedAt
                };
            }

            using var form = new PartyEditForm('S', dto);
            if (form.ShowDialog(FindForm()) == DialogResult.OK)
                _ = LoadDataAsync();
        }

        private async Task DeactivateAsync()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            var visible = _suppliers.Where(s => s.Party != null).ToList();
            if (idx < 0 || idx >= visible.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Suppliers.Msg.PickSupplier"));
                return;
            }

            var party = visible[idx].Party!;
            if (!ConfirmationDialog.Show(this, AppLang.T("Suppliers.Title.Deactivate"), AppLang.T("Suppliers.Msg.DeactivateConfirm", party.Name)))
                return;

            Common.Results.Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var partyService = scope.ServiceProvider.GetRequiredService<clsPartyService>();
                result = await partyService.DeactivatePartyAsync(party.PartyId, SessionState.CurrentUser?.UserId ?? 0); // VULN-26: بوابة خدمية
            }

            UiHelpers.ShowResult(this, result);
            if (result.IsSuccess)
                await LoadDataAsync();
        }
    }
}
