using Common.DTOs.RequestModels;
using Common.DTOs.ViewModel;
using Common.Enums;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// Supplier return (purchase return): enter the original purchase invoice number,
    /// pick return quantities per item, execute the return via clsReturnService.
    /// The PurchaseInvoices permission bit and session validation live in the service;
    /// the UI only hides.
    /// </summary>
    public class SupplierReturnForm : DialogForm
    {
        private TextBox _txtInvoiceId = null!;
        private Button _btnLoad = null!;
        private DataGridView _grid = null!;
        private TextBox _txtNotes = null!;
        private Label _lblInfo = null!;

        private int _originalInvoiceId;
        private List<InvoiceItemVM> _items = new();
        private decimal _invoiceTotal;
        private decimal _invoiceNet;
        private decimal _netRatio;

        // Remaining returnable per product (purchased - previously returned),
        // computed at load time from the same service data the service guard uses.
        // Display/hint only: the final gate remains the clsReturnService guard.
        private Dictionary<int, int> _remainingByProduct = new();

        // VULN-05: active drawer session - refund cash is attributed to it in the
        // Z report (not to the frozen original purchase session). Passed by caller.
        private readonly int? _activeSessionId;

        public bool Returned { get; private set; }

        public SupplierReturnForm(int? activeSessionId = null)
        {
            _activeSessionId = activeSessionId;
            BuildUi();
        }

        /// <summary>Open the form preloaded on a specific purchase invoice
        /// (per-row return action) - the invoice auto-loads on open.</summary>
        public static SupplierReturnForm CreateForInvoice(int invoiceId, int? activeSessionId)
        {
            var form = new SupplierReturnForm(activeSessionId)
            {
                _autoLoadInvoiceId = invoiceId
            };
            form._txtInvoiceId.Text = invoiceId.ToString();
            return form;
        }

        /// <summary>Invoice to auto-load when the form is shown (per-row entry).</summary>
        private int _autoLoadInvoiceId;

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_autoLoadInvoiceId > 0)
            {
                _autoLoadInvoiceId = 0;
                await LoadInvoiceAsync();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // 📖 Build guide - same split-root TLP pattern as ReturnForm:
        //
        //   ┌──────────────────────────────────────┐
        //   │ row0 (Absolute 52): title            │
        //   ├──────────────────────────────────────┤
        //   │ row1 (Absolute 72): invoice no + load │
        //   ├──────────────────────────────────────┤
        //   │ row2 (Percent 100): items grid       │
        //   ├──────────────────────────────────────┤
        //   │ row3 (Absolute 60): info line        │
        //   ├──────────────────────────────────────┤
        //   │ row4 (Absolute 48): reason/notes     │ ← the service stores it on the return invoice
        //   ├──────────────────────────────────────┤
        //   │ row5 (Absolute 64): execute | close  │ ← never clipped
        //   └──────────────────────────────────────┘
        //
        // RTL column flip: column 0 renders rightmost visually.
        // RowCount=1 mandatory on inner TLPs (same trick as ReturnForm).
        // ═══════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            // ── window rules (same as every dialog) ──
            Text = AppLang.T("SupplierReturn.Form.Title");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 680);
            MinimumSize = new Size(560, 500);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            // ══ root: one column + five rows ══
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(16, 12, 16, 12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));   // title
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));   // invoice no row
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // grid
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));   // info line
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));   // notes
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));   // buttons

            // ── title ──
            var lblTitle = new Label
            {
                Text = AppLang.T("SupplierReturn.Title"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };

            // ── invoice number row: label+box right, load button left ──
            var inputRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = AppTheme.CardBg,
                Margin = new Padding(0, 8, 0, 8)
            };
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); // label
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180)); // box
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // load button

            var lblInvoice = new Label
            {
                Text = AppLang.T("SupplierReturn.Label.OriginalInvoice"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                AutoEllipsis = true
            };

            _txtInvoiceId = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                TextAlign = HorizontalAlignment.Center,
                BackColor = AppTheme.FieldBg,
            };

            _btnLoad = AppTheme.MakeButton(AppLang.T("SupplierReturn.Button.Load"), AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            _btnLoad.TextAlign = ContentAlignment.MiddleCenter;
            _btnLoad.Dock = DockStyle.Fill;
            _btnLoad.Click += async (_, _) => await LoadInvoiceAsync();

            inputRow.Controls.Add(lblInvoice, 0, 0);
            inputRow.Controls.Add(_txtInvoiceId, 1, 0);
            inputRow.Controls.Add(_btnLoad, 2, 0);

            // ── items grid (flexible row) ──
            _grid = new DataGridView { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
            UiHelpers.StyleGrid(_grid);
            _grid.ReadOnly = false;
            _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            _grid.Columns.Add("colProduct", AppLang.T("Common.Label.Product"));
            _grid.Columns.Add("colPurchased", AppLang.T("SupplierReturn.Label.Purchased"));
            _grid.Columns.Add("colReturned", AppLang.T("SupplierReturn.Label.Returned"));
            _grid.Columns.Add("colRemaining", AppLang.T("SupplierReturn.Label.Remaining"));
            _grid.Columns.Add("colPrice", AppLang.T("Common.Label.Price"));
            var colReturnQty = new DataGridViewTextBoxColumn { Name = "colReturnQty", HeaderText = AppLang.T("SupplierReturn.Label.ReturnQty"), ReadOnly = false };
            _grid.Columns.Add(colReturnQty);
            _grid.Columns["colProduct"].FillWeight = 30;
            _grid.Columns["colPurchased"].FillWeight = 12;
            _grid.Columns["colReturned"].FillWeight = 13;
            _grid.Columns["colRemaining"].FillWeight = 13;
            _grid.Columns["colPrice"].FillWeight = 12;
            _grid.Columns["colReturnQty"].FillWeight = 20;
            // return qty column is the visual edit affordance (same trick
            // as the cashier cart in PosForm.StyleCartGrid / ReturnForm).
            _grid.Columns["colReturnQty"].DefaultCellStyle.BackColor = AppTheme.FieldBg;
            _grid.Columns["colReturnQty"].DefaultCellStyle.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            _grid.Columns["colReturnQty"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            // input guard: any non-numeric text is rejected before reaching data
            _grid.CellValidating += (_, args) =>
            {
                if (_grid.Columns[args.ColumnIndex].Name != "colReturnQty")
                    return;
                if (!int.TryParse(args.FormattedValue?.ToString()?.Trim() ?? "", out int _))
                    args.Cancel = true;
            };

            // ── info line: grey card updated after load ──
            _lblInfo = new Label
            {
                Text = AppLang.T("SupplierReturn.Info.Initial"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 10F),
                ForeColor = AppTheme.TextMuted,
                BackColor = AppTheme.FieldBg,
                Padding = new Padding(10, 8, 10, 8)
            };

            // ── notes row (reason field - service stores it on the return invoice) ──
            var notesRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = AppTheme.CardBg,
                Margin = new Padding(0, 4, 0, 4)
            };
            notesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            notesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var lblNotes = new Label
            {
                Text = AppLang.T("Common.Label.Notes"),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.TextMuted
            };
            _txtNotes = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = AppTheme.FieldBg
            };
            notesRow.Controls.Add(lblNotes, 0, 0);
            notesRow.Controls.Add(_txtNotes, 1, 0);

            // ── buttons row: execute (right, warning) | close (left) ──
            // 55/45 columns, Dock=Fill. RTL flips: column 0 = rightmost.
            var buttons = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = AppTheme.CardBg,
                Margin = new Padding(0, 8, 0, 0)
            };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

            var btnReturn = AppTheme.MakeButton(AppLang.T("SupplierReturn.Button.Execute"), AppTheme.Warning, hoverColor: Color.FromArgb(180, 83, 9), height: 50);
            btnReturn.Dock = DockStyle.Fill;
            btnReturn.Margin = new Padding(0, 6, 4, 6);
            btnReturn.Click += async (_, _) => await ExecuteReturnAsync();

            var btnCancel = AppTheme.MakeButton(AppLang.T("Common.Button.Close"), AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor, height: 50);
            btnCancel.Dock = DockStyle.Fill;
            btnCancel.Margin = new Padding(4, 6, 0, 6);
            btnCancel.DialogResult = DialogResult.Cancel; // Esc = close

            buttons.Controls.Add(btnReturn, 0, 0);   // column 0 = visually right
            buttons.Controls.Add(btnCancel, 1, 0);   // column 1 = visually left

            // ── assemble root rows (order matches RowStyles above) ──
            root.Controls.Add(lblTitle, 0, 0);
            root.Controls.Add(inputRow, 0, 1);
            root.Controls.Add(_grid, 0, 2);
            root.Controls.Add(_lblInfo, 0, 3);
            root.Controls.Add(notesRow, 0, 4);
            root.Controls.Add(buttons, 0, 5);

            Controls.Add(root);
            CancelButton = btnCancel;
            AcceptButton = _btnLoad; // Enter = load (always the first step)
            ActiveControl = _txtInvoiceId;
        }

        private async Task LoadInvoiceAsync()
        {
            if (!int.TryParse(_txtInvoiceId.Text?.Trim(), out int invoiceId) || invoiceId <= 0)
            {
                UiHelpers.ShowError(this, AppLang.T("SupplierReturn.Msg.InvalidInvoiceId"));
                return;
            }

            try
            {
                using var scope = Program.Services.CreateScope();
                var invoiceService = scope.ServiceProvider.GetRequiredService<clsInvoiceService>();
                var result = await invoiceService.GetInvoiceForPrintAsync(invoiceId);
                if (!result.IsSuccess || result.Data == null)
                {
                    UiHelpers.ShowError(this, result.Message);
                    return;
                }

                if (result.Data.Type != "purchase" || result.Data.Status != "completed")
                {
                    UiHelpers.ShowError(this, AppLang.T("SupplierReturn.Msg.OnlyCompletedPurchase"));
                    return;
                }

                _originalInvoiceId = invoiceId;
                _items = result.Data.Items;
                _invoiceTotal = result.Data.TotalAmount;
                _invoiceNet = result.Data.NetAmount;
                _netRatio = _invoiceTotal > 0 ? _invoiceNet / _invoiceTotal : 0m;

                // Previously returned per product - from the same service the
                // return guard uses, so "remaining returnable" matches what the
                // guard will actually accept.
                Dictionary<int, int> previouslyReturned;
                using (var returnScope = Program.Services.CreateScope())
                {
                    var returnService = returnScope.ServiceProvider.GetRequiredService<clsReturnService>();
                    previouslyReturned = await returnService.GetPreviouslyReturnedQuantitiesAsync(
                        invoiceId, enInvoicesType.supplier_return);
                }

                // Remaining per product summed across its lines (stock withdrawal at
                // purchase splits the line into a row per batch, so the product may
                // repeat across lines) - the full share stays available to return.
                _remainingByProduct = _items
                    .GroupBy(i => i.ProductId)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Sum(i => i.Quantity) - previouslyReturned.GetValueOrDefault(g.Key));

                _grid.Rows.Clear();
                foreach (var item in _items)
                    _grid.Rows.Add(item.ProductName, item.Quantity,
                        previouslyReturned.GetValueOrDefault(item.ProductId),
                        _remainingByProduct.GetValueOrDefault(item.ProductId),
                        UiHelpers.Money(item.ItemPrice * _netRatio), 0);

                _lblInfo.Text = AppLang.T("SupplierReturn.Info.Loaded", invoiceId,
                    result.Data.PartyName ?? AppLang.T("Common.Party.Unknown"));
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("SupplierReturn.Msg.LoadFailed"));
            }
        }

        private async Task ExecuteReturnAsync()
        {
            if (_originalInvoiceId <= 0 || _items.Count == 0)
            {
                UiHelpers.ShowError(this, AppLang.T("SupplierReturn.Msg.LoadFirst"));
                return;
            }

            var returnItems = new List<DtoReturnItem>();
            decimal grossTotal = 0;

            // Input is aggregated per product first (the product may repeat across
            // lines for its batches) then checked against remaining returnable -
            // same guard as the service (display here, final decision there).
            var enteredByProduct = new Dictionary<int, int>();

            for (int i = 0; i < _grid.Rows.Count && i < _items.Count; i++)
            {
                if (!int.TryParse(_grid.Rows[i].Cells["colReturnQty"].Value?.ToString(), out int qty) || qty <= 0)
                    continue;

                var item = _items[i];
                enteredByProduct[item.ProductId] = enteredByProduct.GetValueOrDefault(item.ProductId) + qty;
                returnItems.Add(new DtoReturnItem { ProductId = item.ProductId, Quantity = qty });
                grossTotal += item.ItemPrice * qty;
            }

            foreach (var entered in enteredByProduct)
            {
                int remaining = _remainingByProduct.GetValueOrDefault(entered.Key);
                if (entered.Value > remaining)
                {
                    string productName = _items.First(i => i.ProductId == entered.Key).ProductName;
                    UiHelpers.ShowError(this, AppLang.T("SupplierReturn.Msg.QtyExceedsRemaining", productName, remaining));
                    return;
                }
            }

            decimal returnTotal = Math.Round(grossTotal * _netRatio, 2, MidpointRounding.AwayFromZero);

            if (returnItems.Count == 0)
            {
                UiHelpers.ShowError(this, AppLang.T("SupplierReturn.Msg.OneItemRequired"));
                return;
            }

            bool confirmed = ConfirmationDialog.Show(this, AppLang.T("SupplierReturn.Execute.Title"),
                AppLang.T("SupplierReturn.Execute.Confirm", returnItems.Count, UiHelpers.Money(returnTotal)));
            if (!confirmed)
                return;

            var request = new DtoReturnRequest
            {
                OriginalInvoiceId = _originalInvoiceId,
                UserId = SessionState.CurrentUser?.UserId ?? 0,
                SessionId = _activeSessionId, // VULN-05: active drawer session
                Items = returnItems,
                Notes = string.IsNullOrWhiteSpace(_txtNotes.Text) ? null : _txtNotes.Text.Trim()
            };

            Result<int> result;
            _btnLoad.Enabled = false;
            try
            {
                using (var scope = Program.Services.CreateScope())
                {
                    var returnService = scope.ServiceProvider.GetRequiredService<clsReturnService>();
                    result = await returnService.CreateSupplierReturnAsync(request);
                }
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("SupplierReturn.Msg.Failed"));
                return;
            }
            finally
            {
                _btnLoad.Enabled = true;
            }

            if (!result.IsSuccess)
            {
                UiHelpers.ShowError(this, string.IsNullOrWhiteSpace(result.Message) ? AppLang.T("SupplierReturn.Msg.Failed") : result.Message);
                return;
            }

            // Success shown only after commit - ShowInfo after result.IsSuccess.
            UiHelpers.ShowInfo(this, AppLang.T("SupplierReturn.Msg.Done", result.Data));
            Returned = true;
            DialogResult = DialogResult.OK;
        }
    }
}
