using Common.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>قسم العملاء: عرض، بحث، إضافة/تعديل، تعطيل.</summary>
    internal class CustomersControl : SectionListControl
    {
        private List<Customer> _customers = new();

        protected override string Title => "العملاء";

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var customerService = scope.ServiceProvider.GetRequiredService<clsCustomerService>();
                _customers = await customerService.GetCustomersListAsync(TxtSearch.Text?.Trim() ?? "");

                Grid.Columns.Clear();
                Grid.Columns.Add("colName", "الاسم");
                Grid.Columns.Add("colPhone", "الجوال");
                Grid.Columns.Add("colBalance", "الرصيد");
                Grid.Columns.Add("colCredit", "حد الائتمان");
                Grid.Columns.Add("colStatus", "الحالة");
                Grid.Columns["colName"].FillWeight = 30;
                Grid.Columns["colPhone"].FillWeight = 20;
                Grid.Columns["colBalance"].FillWeight = 15;
                Grid.Columns["colCredit"].FillWeight = 15;
                Grid.Columns["colStatus"].FillWeight = 12;

                var colDetails = new DataGridViewButtonColumn
                {
                    Name = "colDetails",
                    HeaderText = "",
                    Text = "التفاصيل",
                    UseColumnTextForButtonValue = true,
                    FlatStyle = FlatStyle.Flat,
                    FillWeight = 8
                };
                colDetails.DefaultCellStyle.ForeColor = AppTheme.Primary;
                colDetails.DefaultCellStyle.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
                Grid.Columns.Add(colDetails);

                Grid.Rows.Clear();
                foreach (var c in _customers.Where(c => c.Party != null))
                    Grid.Rows.Add(c.Party!.Name, c.Party.Phone1 ?? "-", UiHelpers.Money(c.Balance ?? 0),
                        UiHelpers.Money(c.CreditLimit ?? 0), c.Party.IsActive ? "نشط" : "معطل");
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل العملاء.");
            }
        }

        protected override void BuildToolbar()
        {
            AddToolbarButton("إضافة عميل", AppTheme.Primary, AppTheme.PrimaryDark, (_, _) => EditParty(null), 140);
            AddToolbarGap();
            AddToolbarButton("تعديل", AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => EditSelected(), 110);
            AddToolbarGap();
            AddToolbarButton("تعطيل", AppTheme.Danger, Color.FromArgb(185, 28, 28), async (_, _) => await DeactivateAsync(), 110);

            if (SessionState.IsAdmin)
            {
                AddToolbarGap();
                AddToolbarButton("إعادة تفعيل", AppTheme.Success, Color.FromArgb(21, 128, 61),
                    async (_, _) => await ActivateAsync(), 130);
            }

            Grid.CellClick += Grid_CellClick;
        }

        private void Grid_CellClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _customers.Count) return;
            if (Grid.Columns[e.ColumnIndex].Name != "colDetails") return;

            var visible = _customers.Where(c => c.Party != null).ToList();
            if (e.RowIndex >= visible.Count) return;

            var customer = visible[e.RowIndex];
            var party = customer.Party!;
            using var form = new PartyDetailsForm(party,
                ("الرصيد الحالي", UiHelpers.Money(customer.Balance ?? 0)),
                ("حد الائتمان", UiHelpers.Money(customer.CreditLimit ?? 0)));
            form.ShowDialog(FindForm());
        }

        private async Task ActivateAsync()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            var visible = _customers.Where(c => c.Party != null).ToList();
            if (idx < 0 || idx >= visible.Count)
            {
                UiHelpers.ShowError(this, "اختر عميلًا من القائمة.");
                return;
            }

            var party = visible[idx].Party!;
            if (party.IsActive)
            {
                UiHelpers.ShowInfo(this, $"العميل '{party.Name}' نشط بالفعل.");
                return;
            }

            if (!ConfirmationDialog.Show(this, "إعادة تفعيل عميل",
                    $"سيتم إعادة تفعيل العميل '{party.Name}'. هل أنت متأكد؟", dangerStyle: false))
                return;

            Common.Results.Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var partyService = scope.ServiceProvider.GetRequiredService<clsPartyService>();
                result = await partyService.ActivatePartyAsync(party.PartyId);
            }

            UiHelpers.ShowResult(this, result);
            if (result.IsSuccess)
                await LoadDataAsync();
        }

        private void EditSelected()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            var visible = _customers.Where(c => c.Party != null).ToList();
            if (idx < 0 || idx >= visible.Count)
            {
                UiHelpers.ShowError(this, "اختر عميلًا من القائمة.");
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

            using var form = new PartyEditForm('C', dto);
            if (form.ShowDialog(FindForm()) == DialogResult.OK)
                _ = LoadDataAsync();
        }

        private async Task DeactivateAsync()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            var visible = _customers.Where(c => c.Party != null).ToList();
            if (idx < 0 || idx >= visible.Count)
            {
                UiHelpers.ShowError(this, "اختر عميلًا من القائمة.");
                return;
            }

            var party = visible[idx].Party!;
            if (!ConfirmationDialog.Show(this, "تعطيل عميل", $"سيتم تعطيل العميل '{party.Name}'. هل أنت متأكد؟"))
                return;

            Common.Results.Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var partyService = scope.ServiceProvider.GetRequiredService<clsPartyService>();
                result = await partyService.DeactivatePartyAsync(party.PartyId);
            }

            UiHelpers.ShowResult(this, result);
            if (result.IsSuccess)
                await LoadDataAsync();
        }
    }
}
