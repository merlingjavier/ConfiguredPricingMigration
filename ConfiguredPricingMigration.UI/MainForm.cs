using ConfiguredPricingMigration.Core;

namespace ConfiguredPricingMigration.UI;

public sealed class MainForm : Form
{
    private static readonly Color PrimaryButtonColor = Color.FromArgb(0, 99, 163);
    private static readonly Color SecondaryButtonColor = Color.FromArgb(230, 238, 246);
    private static readonly Color WarningButtonColor = Color.FromArgb(180, 82, 24);
    private readonly TextBox _sqlConnection = new()
    {
        Dock = DockStyle.Fill,
        UseSystemPasswordChar = true
    };
    private readonly TextBox _migrationMongoConnection = new()
    {
        Dock = DockStyle.Fill,
        UseSystemPasswordChar = true
    };
    private readonly TextBox _migrationMongoDatabase = new()
    {
        Dock = DockStyle.Fill,
        Text = "ConfiguredPricingMigration"
    };
    private readonly TextBox _targetMongoConnection = new()
    {
        Dock = DockStyle.Fill,
        UseSystemPasswordChar = true
    };
    private readonly TextBox _targetMongoDatabase = new()
    {
        Dock = DockStyle.Fill,
        Text = "ConfiguredPricing"
    };
    private readonly TextBox _runId = new()
    {
        Width = 420,
        Text = NewRunId()
    };
    private readonly Button _generateRunId = new()
    {
        Text = "Generar Run ID",
        AutoSize = true
    };
    private readonly ComboBox _runs = new()
    {
        Width = 420,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly Button _loadRuns = new()
    {
        Text = "Actualizar Runs",
        AutoSize = true
    };
    private readonly RadioButton _ordinarySchema = CreateSchemaRadioButton("Ordinario", isChecked: true);
    private readonly RadioButton _baselineSchema = CreateSchemaRadioButton("Línea Base");
    private readonly RadioButton _specialSchema = CreateSchemaRadioButton("Especial");
    private Control _schemaTypeCards = null!;
    private readonly TextBox _productIds = new()
    {
        Dock = DockStyle.Fill
    };
    private readonly NumericUpDown _batchSize = new()
    {
        Dock = DockStyle.Left,
        Minimum = 1_000,
        Maximum = 100_000,
        Increment = 1_000,
        Value = 10_000
    };
    private readonly RichTextBox _log = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = Color.White,
        Font = new Font("Consolas", 9),
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly Button _test = new()
    {
        Text = "Probar conexiones",
        AutoSize = true
    };
    private readonly Button _migrate = new()
    {
        Text = "Ejecutar migración",
        AutoSize = true
    };
    private readonly Button _stop = new()
    {
        Text = "Detener migración",
        AutoSize = true,
        Enabled = false
    };
    private readonly Button _validate = new()
    {
        Text = "Validar carga",
        AutoSize = true
    };
    private readonly Button _publish = new()
    {
        Text = "Transferir y publicar Run validado",
        AutoSize = true
    };
    private readonly ComboBox _previewRuns = new()
    {
        Width = 300,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly Button _loadPreviewRuns = new()
    {
        Text = "Actualizar Runs",
        AutoSize = true
    };
    private readonly RadioButton _previewOrdinarySchema = CreateSchemaRadioButton("Ordinario", isChecked: true);
    private readonly RadioButton _previewBaselineSchema = CreateSchemaRadioButton("Línea Base");
    private readonly RadioButton _previewSpecialSchema = CreateSchemaRadioButton("Especial");
    private readonly Label _previewProductsLabel = CreatePreviewFilterLabel("Producto");
    private readonly ComboBox _previewProducts = new()
    {
        Width = 220,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly Label _previewBranchesLabel = CreatePreviewFilterLabel("Establecimientos");
    private readonly ComboBox _previewBranches = new()
    {
        Width = 220,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly Label _previewProfilesLabel = CreatePreviewFilterLabel("Perfiles");
    private readonly ComboBox _previewProfiles = new()
    {
        Width = 220,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly Label _previewValueLabel = CreatePreviewFilterLabel("Valor");
    private readonly ComboBox _previewValue = new()
    {
        Width = 130,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly Label _previewSummary = new()
    {
        AutoSize = true,
        Padding = new Padding(8, 6, 0, 0)
    };
    private readonly DataGridView _previewGrid = new()
    {
        Dock = DockStyle.Fill,
        AutoGenerateColumns = false,
        AllowUserToAddRows = false,
        ReadOnly = true,
        RowHeadersVisible = false
    };
    private CancellationTokenSource? _migrationCancellation;
    private MigrationPreview? _preview;
    private bool _loadingPreview;
    private bool _loadingRuns;
    private readonly ToolTip _fieldToolTip = new()
    {
        AutoPopDelay = 10_000,
        InitialDelay = 400,
        ReshowDelay = 200,
        ShowAlways = true
    };

    public MainForm(string? environmentFile)
    {
        Text = "Configured Pricing Migration";
        ClientSize = new Size(1120, 900);
        MinimumSize = new Size(980, 820);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9);
        LoadEnvironmentConfiguration();

        var fields = CreateFieldsPanel();
        _schemaTypeCards = CreateSchemaTypeCards();
        AddField(fields, "Tipo de esquema", _schemaTypeCards);
        var runIdField = CreateExpandableFieldPanel(_runId, _generateRunId);
        AddFieldWithHelp(
            fields,
            "Run ID",
            "Identifica esta ejecución y permite retomarla.",
            runIdField);
        var runSelector = CreateExpandableFieldPanel(_runs, _loadRuns);
        AddFieldWithHelp(
            fields,
            "Runs existentes",
            "Seleccione uno para continuar o revisar su estado.",
            runSelector);
        AddFieldWithHelp(
            fields,
            "Productos (opcional)",
            "Use IDs separados por comas; vacío procesa los elegibles.",
            _productIds);
        AddFieldWithHelp(
            fields,
            "Tamaño de lote",
            "Filas por bloque; 10,000 es el valor recomendado.",
            _batchSize);

        ConfigureActionButtons();
        var actions = CreateActionsPanel();
        actions.Controls.AddRange([_test, _migrate, _stop, _validate, _publish]);
        var setup = CreateSectionCard(
            "Preparar migración",
            "Las conexiones se administran mediante variables de entorno o el archivo .env local.",
            fields);
        var actionCard = CreateSectionCard(
            "Acciones",
            "Pruebe la configuración antes de ejecutar. Valide el Run antes de publicarlo.",
            actions);
        var activity = CreateSectionCard(
            "Actividad de la migración",
            "Mensajes, avance y resultados de las operaciones ejecutadas.",
            _log,
            fillBody: true);
        activity.Dock = DockStyle.Fill;
        activity.MinimumSize = new Size(0, 320);
        _log.MinimumSize = new Size(0, 240);

        var migrationLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16),
            BackColor = Color.FromArgb(245, 247, 250)
        };
        migrationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        migrationLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        migrationLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        migrationLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        migrationLayout.Controls.Add(setup, 0, 0);
        migrationLayout.Controls.Add(actionCard, 0, 1);
        migrationLayout.Controls.Add(activity, 0, 2);

        Controls.Add(migrationLayout);
        Controls.Add(CreateHeader(CreateConfigurationStatus()));
        WireEvents();
        UpdateProductFilterAvailability();

        if (environmentFile is not null)
        {
            Log($"Configuracion cargada desde {environmentFile}");
        }
    }

    private void LoadEnvironmentConfiguration()
    {
        _sqlConnection.Text = GetEnvironmentValue("SQL_CONNECTION_STRING");
        _migrationMongoConnection.Text = GetEnvironmentValue("MIGRATION_MONGO_CONNECTION_STRING");
        _migrationMongoDatabase.Text = GetEnvironmentValue(
            "MIGRATION_MONGO_DATABASE",
            _migrationMongoDatabase.Text);
        _targetMongoConnection.Text = GetEnvironmentValue("TARGET_MONGO_CONNECTION_STRING");
        _targetMongoDatabase.Text = GetEnvironmentValue("TARGET_MONGO_DATABASE", _targetMongoDatabase.Text);
        _productIds.Text = GetEnvironmentValue("MIGRATION_PRODUCT_IDS");

        if (TryGetConfiguredBatchSize(out var batchSize))
        {
            _batchSize.Value = batchSize;
        }
    }

    private static string GetEnvironmentValue(string variableName, string defaultValue = "")
    {
        return Environment.GetEnvironmentVariable(variableName) ?? defaultValue;
    }

    private bool TryGetConfiguredBatchSize(out int batchSize)
    {
        var isValid = int.TryParse(
            Environment.GetEnvironmentVariable("MIGRATION_BATCH_SIZE"),
            out batchSize);

        return isValid && batchSize >= _batchSize.Minimum && batchSize <= _batchSize.Maximum;
    }

    private string CreateConfigurationStatus()
    {
        var isConfigured = !string.IsNullOrWhiteSpace(_sqlConnection.Text)
            && !string.IsNullOrWhiteSpace(_migrationMongoConnection.Text)
            && !string.IsNullOrWhiteSpace(_migrationMongoDatabase.Text)
            && !string.IsNullOrWhiteSpace(_targetMongoConnection.Text)
            && !string.IsNullOrWhiteSpace(_targetMongoDatabase.Text);

        return isConfigured
            ? "Configuración detectada. Las credenciales se administran de forma segura y no se muestran aquí."
            : "Configuración pendiente. Revise las variables de entorno o el archivo .env local antes de continuar.";
    }

    private static Control CreateHeader(string configurationStatus)
    {
        var title = new Label
        {
            Text = "Migración de Configured Pricing",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 18),
            ForeColor = Color.White,
            Location = new Point(24, 18)
        };
        var subtitle = new Label
        {
            Text = "Ejecute, valide y publique una carga inicial de forma controlada.",
            AutoSize = true,
            ForeColor = Color.FromArgb(220, 235, 248),
            Location = new Point(26, 52)
        };
        var status = new Label
        {
            Text = configurationStatus,
            AutoSize = true,
            ForeColor = Color.FromArgb(238, 246, 252),
            Location = new Point(26, 76)
        };
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 110,
            BackColor = Color.FromArgb(19, 67, 104)
        };
        header.Controls.AddRange([title, subtitle, status]);
        return header;
    }

    private void ConfigureActionButtons()
    {
        StyleActionButton(_migrate, PrimaryButtonColor, Color.White);
        StyleActionButton(_stop, WarningButtonColor, Color.White);
        StyleActionButton(_test, SecondaryButtonColor, Color.FromArgb(25, 62, 91));
        StyleActionButton(_validate, SecondaryButtonColor, Color.FromArgb(25, 62, 91));
        StyleActionButton(_publish, SecondaryButtonColor, Color.FromArgb(25, 62, 91));
    }

    private static void StyleActionButton(Button button, Color background, Color foreground)
    {
        button.AutoSize = false;
        button.Width = TextRenderer.MeasureText(button.Text, button.Font).Width + 28;
        button.Height = 36;
        button.Padding = new Padding(10, 0, 10, 0);
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = background;
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.Margin = new Padding(0, 0, 8, 0);
    }

    private static FlowLayoutPanel CreateActionsPanel()
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(14, 4, 14, 14)
        };
    }

    private static Panel CreateSectionCard(
        string title,
        string hint,
        Control body,
        bool fillBody = false)
    {
        var titleLabel = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 11),
            ForeColor = Color.FromArgb(30, 55, 75),
            Location = new Point(14, 10)
        };
        var hintLabel = new Label
        {
            Text = hint,
            AutoSize = true,
            ForeColor = Color.FromArgb(85, 99, 112),
            Location = new Point(14, 34)
        };
        var heading = new Panel
        {
            Dock = DockStyle.Top,
            Height = 58,
            BackColor = Color.FromArgb(239, 244, 248)
        };
        heading.Controls.AddRange([titleLabel, hintLabel]);

        var card = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = !fillBody,
            Margin = new Padding(0, 0, 0, 12),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White
        };
        body.Dock = fillBody ? DockStyle.Fill : DockStyle.Top;
        card.Controls.Add(body);
        card.Controls.Add(heading);
        return card;
    }

    private void WireEvents()
    {
        _test.Click += async (_, _) => await TestConnectionsAsync();
        _migrate.Click += async (_, _) => await MigrateOrPauseAsync();
        _stop.Click += (_, _) => _migrationCancellation?.Cancel();
        _generateRunId.Click += (_, _) => _runId.Text = NewRunId();
        _validate.Click += async (_, _) => await ValidateMigrationAsync();
        _publish.Click += async (_, _) => await TransferAndPublishAsync();
        _loadRuns.Click += async (_, _) => await LoadRunsAsync();
        _runs.SelectedIndexChanged += async (_, _) => await SelectRunAsync();
        _runId.TextChanged += (_, _) => ClearUnmatchedSelectedRun();
        _ordinarySchema.CheckedChanged += (_, _) =>
            UpdateSchemaSelection(_ordinarySchema, _baselineSchema, _specialSchema);
        _baselineSchema.CheckedChanged += (_, _) =>
            UpdateSchemaSelection(_baselineSchema, _ordinarySchema, _specialSchema);
        _specialSchema.CheckedChanged += (_, _) =>
            UpdateSchemaSelection(_specialSchema, _ordinarySchema, _baselineSchema);
        Shown += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_migrationMongoConnection.Text))
            {
                await LoadRunsAsync();
            }
        };
    }

    private async Task SelectRunAsync()
    {
        if (_loadingRuns || _runs.SelectedItem is not RunChoice run)
        {
            return;
        }

        _runId.Text = run.Id;
        SelectSchemaType(run.TypeSchema);
    }

    private async Task PreviewSchemaChangedAsync(
        RadioButton selectedSchema,
        RadioButton otherSchema,
        RadioButton anotherSchema)
    {
        if (!selectedSchema.Checked)
        {
            return;
        }

        otherSchema.Checked = false;
        anotherSchema.Checked = false;

        if (_loadingPreview)
        {
            return;
        }

        _preview = null;
        UpdatePreviewFilterVisibility(SelectedPreviewSchema());
        ClearPreviewGrid("Seleccione un Run consolidado para cargar la matriz.");
        await LoadPreviewRunsAsync();
    }

    private async Task SelectPreviewRunAsync()
    {
        if (_loadingPreview || _previewRuns.SelectedItem is not RunChoice run)
        {
            return;
        }

        SelectMainRun(run.Id);
        SelectSchemaType(run.TypeSchema);
        await LoadPreviewAsync();
    }

    private void ClearUnmatchedSelectedRun()
    {
        if (_loadingRuns
            || _runs.SelectedItem is not RunChoice run
            || run.Id == _runId.Text.Trim())
        {
            return;
        }

        _runs.SelectedIndex = -1;
    }

    private void UpdateSchemaSelection(
        RadioButton selectedSchema,
        RadioButton otherSchema,
        RadioButton anotherSchema)
    {
        if (selectedSchema.Checked)
        {
            otherSchema.Checked = false;
            anotherSchema.Checked = false;
        }

        UpdateProductFilterAvailability();
    }

    private void UpdateProductFilterAvailability()
    {
        _productIds.Enabled = _ordinarySchema.Checked;
    }

    private void BindPreviewFiltersWhenReady()
    {
        if (!_loadingPreview)
        {
            BindPreviewFilters();
        }
    }

    private void BindPreviewGridWhenReady()
    {
        if (!_loadingPreview)
        {
            BindPreviewGrid();
        }
    }

    private static RadioButton CreateSchemaRadioButton(string text, bool isChecked = false)
    {
        return new RadioButton
        {
            Text = text,
            AutoSize = true,
            Checked = isChecked,
            Font = new Font("Segoe UI", 9, FontStyle.Bold)
        };
    }

    private static Label CreatePreviewFilterLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(12, 6, 0, 0)
        };
    }

    private static TableLayoutPanel CreateFieldsPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(12)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        return panel;
    }

    private static FlowLayoutPanel CreateFlowPanel()
    {
        return new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill
        };
    }

    private static TableLayoutPanel CreateExpandableFieldPanel(Control input, Control action)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 0, 8, 0);
        action.AutoSize = false;
        action.Width = TextRenderer.MeasureText(action.Text, action.Font).Width + 24;
        action.Dock = DockStyle.Fill;
        action.Margin = Padding.Empty;

        if (action is Button button)
        {
            button.TextAlign = ContentAlignment.MiddleCenter;
        }

        panel.Controls.Add(input, 0, 0);
        panel.Controls.Add(action, 1, 0);
        return panel;
    }

    private static void AddField(TableLayoutPanel panel, string label, Control control)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(CreateFieldLabel(label), 0, panel.RowCount);
        panel.Controls.Add(control, 1, panel.RowCount++);
    }

    private void AddFieldWithHelp(
        TableLayoutPanel panel,
        string label,
        string help,
        Control control)
    {
        var labelControl = CreateFieldLabel(label);
        _fieldToolTip.SetToolTip(labelControl, help);
        _fieldToolTip.SetToolTip(control, help);

        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(labelControl, 0, panel.RowCount);
        panel.Controls.Add(control, 1, panel.RowCount++);
    }

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 6, 6, 6)
        };
    }

    private static void AddField(TableLayoutPanel panel, Label label, Control control)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(label, 0, panel.RowCount);
        panel.Controls.Add(control, 1, panel.RowCount++);
    }

    private string SelectedTypeSchema()
    {
        return _ordinarySchema.Checked
            ? "ORDINARY"
            : _baselineSchema.Checked
                ? "BASELINE"
                : "SPECIAL";
    }

    private string SelectedPreviewSchema()
    {
        return _previewOrdinarySchema.Checked
            ? "ORDINARY"
            : _previewBaselineSchema.Checked
                ? "BASELINE"
                : "SPECIAL";
    }

    private Control CreateSchemaTypeCards() => CreateSchemaTypeCards(_ordinarySchema, _baselineSchema, _specialSchema);

    private static Control CreateSchemaTypeCards(RadioButton ordinary, RadioButton baseline, RadioButton special)
    {
        var cards = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1
        };
        for (var index = 0; index < 3; index++)
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        }

        cards.Controls.Add(SchemaCard(ordinary, "Tarifario completo con modos estándar y negociable."), 0, 0);
        cards.Controls.Add(SchemaCard(baseline, "Tasas 35 y 62; un esquema global."), 1, 0);
        cards.Controls.Add(SchemaCard(special, "Tasa 64 por monto y establecimiento; global."), 2, 0);
        return cards;
    }

    private static Control SchemaCard(RadioButton selector, string description)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            Height = 72,
            Margin = new Padding(3),
            Padding = new Padding(6),
            BorderStyle = BorderStyle.FixedSingle,
            Cursor = Cursors.Hand
        };
        selector.Location = new Point(6, 5);
        var detail = new Label
        {
            Text = description,
            AutoSize = true,
            Location = new Point(26, 29),
            MaximumSize = new Size(205, 0),
            Cursor = Cursors.Hand
        };
        card.Controls.Add(selector);
        card.Controls.Add(detail);
        card.Click += (_, _) => selector.Checked = true;
        detail.Click += (_, _) => selector.Checked = true;
        return card;
    }

    private MigrationOptions CreateMigrationOptions() => new()
    {
        SqlConnectionString = _sqlConnection.Text.Trim(),
        MigrationMongoConnectionString = _migrationMongoConnection.Text.Trim(),
        MigrationMongoDatabase = _migrationMongoDatabase.Text.Trim(),
        TargetMongoConnectionString = _targetMongoConnection.Text.Trim(),
        TargetMongoDatabase = _targetMongoDatabase.Text.Trim(),
        BatchSize = Decimal.ToInt32(_batchSize.Value),
        ProductIds = _ordinarySchema.Checked
            ? ParseProductIds(_productIds.Text)
            : Array.Empty<int>(),
        TypeSchema = SelectedTypeSchema()
    };

    private IProgress<MigrationProgress> CreateProgressReporter() =>
        new Progress<MigrationProgress>(progress =>
            Log($"[{progress.Stage}] {progress.Message} Procesadas={progress.Processed}; " +
                $"Cargadas={progress.Loaded}; Rechazadas={progress.Rejected}"));

    private async Task LoadPreviewAsync()
    {
        await RunAsync(async (service, token) =>
        {
            _preview = await service.GetPreviewAsync(_runId.Text.Trim(), token);
            if (_preview.RateSets.Count == 0)
            {
                throw new InvalidOperationException(
                    "El Run ID no tiene tarifarios consolidados. Ejecute el ETL antes de cargar la vista previa.");
            }

            var ordinary = _preview.TypeSchema == "ORDINARY";
            UpdatePreviewFilterVisibility(_preview.TypeSchema);
            _loadingPreview = true;
            _previewProducts.DataSource = _preview.Products
                .Select(product => new PreviewProductChoice(product))
                .ToList();
            _previewProducts.DisplayMember = nameof(PreviewProductChoice.Display);
            _previewProducts.SelectedIndex = -1;
            _previewBranches.DataSource = null;
            _previewProfiles.DataSource = null;
            _previewGrid.Columns.Clear();
            _previewGrid.Rows.Clear();
            _loadingPreview = false;
            _previewSummary.Text = ordinary
                ? "Seleccione un producto para cargar la matriz."
                : "Seleccione los filtros aplicables para cargar la matriz.";

            if (!ordinary)
            {
                BindPreviewFilters();
            }
        }, "cargar vista previa");
    }

    private async Task<bool> LoadRunsAsync()
    {
        return await RunAsync(async (service, token) =>
        {
            var runs = await service.GetRunsAsync(token);
            _loadingRuns = true;
            _runs.DataSource = runs.Select(run => new RunChoice(run)).ToList();
            _runs.DisplayMember = nameof(RunChoice.Display);
            var currentRun = FindRunChoice(_runs.DataSource, _runId.Text.Trim());
            if (currentRun is not null)
            {
                _runs.SelectedItem = currentRun;
            }
            else
            {
                _runs.SelectedIndex = -1;
            }

            _loadingRuns = false;
            if (currentRun is not null)
            {
                SelectSchemaType(currentRun.TypeSchema);
            }

        }, "actualizar lista de Runs");
    }

    private async Task LoadPreviewRunsAsync()
    {
        await RunAsync(async (service, token) =>
        {
            var selectedSchema = SelectedPreviewSchema();
            var runs = (await service.GetPreviewRunsAsync(token))
                .Where(run => run.TypeSchema == selectedSchema)
                .ToList();
            _loadingPreview = true;
            _previewRuns.DataSource = runs.Select(run => new RunChoice(run)).ToList();
            _previewRuns.DisplayMember = nameof(RunChoice.Display);
            var currentRun = FindRunChoice(_previewRuns.DataSource, _runId.Text.Trim());
            if (currentRun is not null)
            {
                _previewRuns.SelectedItem = currentRun;
            }
            else
            {
                _previewRuns.SelectedIndex = -1;
            }

            _loadingPreview = false;
            if (currentRun is not null)
            {
                SelectSchemaType(currentRun.TypeSchema);
            }

            _previewSummary.Text = runs.Count == 0
                ? $"No hay Runs consolidados para {TypeSchemaLabel(selectedSchema)}."
                : $"Runs consolidados disponibles: {runs.Count}.";
        }, "actualizar Runs consolidados");
    }

    private void SelectMainRun(string runId)
    {
        _loadingRuns = true;
        _runId.Text = runId;
        var run = FindRunChoice(_runs.DataSource, runId);
        if (run is not null)
        {
            _runs.SelectedItem = run;
        }
        _loadingRuns = false;
    }

    private void SelectSchemaType(string typeSchema)
    {
        _ordinarySchema.Checked = typeSchema == "ORDINARY";
        _baselineSchema.Checked = typeSchema == "BASELINE";
        _specialSchema.Checked = typeSchema == "SPECIAL";
    }

    private void SelectPreviewSchema(string typeSchema)
    {
        _loadingPreview = true;
        _previewOrdinarySchema.Checked = typeSchema == "ORDINARY";
        _previewBaselineSchema.Checked = typeSchema == "BASELINE";
        _previewSpecialSchema.Checked = typeSchema == "SPECIAL";
        _loadingPreview = false;
        UpdatePreviewFilterVisibility(typeSchema);
    }

    private void UpdatePreviewFilterVisibility(string typeSchema)
    {
        var ordinary = typeSchema == "ORDINARY";
        var needsBranch = typeSchema is "ORDINARY" or "SPECIAL";
        _previewProductsLabel.Visible = ordinary;
        _previewProducts.Visible = ordinary;
        _previewBranchesLabel.Visible = needsBranch;
        _previewBranches.Visible = needsBranch;
        _previewProfilesLabel.Visible = ordinary;
        _previewProfiles.Visible = ordinary;
    }

    private void SelectPreviewRun(string runId)
    {
        _loadingPreview = true;
        var run = FindRunChoice(_previewRuns.DataSource, runId);
        if (run is not null)
        {
            _previewRuns.SelectedItem = run;
        }
        _loadingPreview = false;
    }

    private static RunChoice? FindRunChoice(object? dataSource, string runId)
    {
        return (dataSource as IEnumerable<RunChoice>)?.FirstOrDefault(choice => choice.Id == runId);
    }

    private void BindPreviewFilters()
    {
        if (_preview is null)
        {
            return;
        }

        _loadingPreview = true;
        var ordinary = _preview.TypeSchema == "ORDINARY";
        var needsBranch = _preview.TypeSchema is "ORDINARY" or "SPECIAL";
        var productId = ordinary
            ? (_previewProducts.SelectedItem as PreviewProductChoice)?.Id
            : _preview.Products.FirstOrDefault()?.Id;
        if (string.IsNullOrEmpty(productId))
        {
            _loadingPreview = false;
            ClearPreviewGrid(
                ordinary
                    ? "Seleccione un producto para cargar la matriz."
                    : "No se encontró el esquema global consolidado.");
            return;
        }

        var sets = _preview.RateSets
            .Where(set => set.ProductId == productId)
            .Where(set => !ordinary || IsDefaultOrdinaryRateSet(set))
            .ToList();
        _previewBranches.DataSource = needsBranch
            ? FilterPreviewGroups("branch", sets)
            : new List<PreviewGroupChoice>();
        _previewBranches.DisplayMember = nameof(PreviewGroupChoice.Display);
        _previewProfiles.DataSource = ordinary
            ? FilterPreviewGroups("internalRating", sets)
            : new List<PreviewGroupChoice>();
        _previewProfiles.DisplayMember = nameof(PreviewGroupChoice.Display);
        _loadingPreview = false;

        var hasNoBranchGroups = needsBranch && _previewBranches.Items.Count == 0;
        var hasNoProfileGroups = ordinary && _previewProfiles.Items.Count == 0;
        if (hasNoBranchGroups || hasNoProfileGroups)
        {
            ClearPreviewGrid(
                hasNoBranchGroups
                    ? "No hay grupos de establecimientos para previsualizar."
                    : "No hay grupos de perfil para previsualizar.");
            return;
        }

        BindPreviewGrid();
    }

    private static bool IsDefaultOrdinaryRateSet(MigrationPreviewRateSet rateSet)
    {
        return rateSet.Insurance
            && rateSet.CurrencyId == 1
            && rateSet.PersonTypeId == 1;
    }

    private List<PreviewGroupChoice> FilterPreviewGroups(
        string dimensionKey,
        IReadOnlyCollection<MigrationPreviewRateSet> rateSets)
    {
        return _preview!.Groups
            .Where(group => group.DimensionKey == dimensionKey)
            .Where(group => rateSets.Any(rateSet => IsGroupUsedByRateSet(dimensionKey, group, rateSet)))
            .Select(group => new PreviewGroupChoice(group))
            .OrderBy(group => group.Display)
            .ToList();
    }

    private static bool IsGroupUsedByRateSet(
        string dimensionKey,
        MigrationPreviewGroup group,
        MigrationPreviewRateSet rateSet)
    {
        return dimensionKey == "branch"
            ? rateSet.BranchGroupId == group.Id
            : rateSet.InternalRatingGroupId == group.Id;
    }

    private void BindPreviewGrid()
    {
        if (_preview is null)
        {
            ClearPreviewGrid("Seleccione un producto para cargar la matriz.");
            return;
        }
        var ordinary = _preview.TypeSchema == "ORDINARY";
        var special = _preview.TypeSchema == "SPECIAL";
        var product = GetSelectedPreviewProduct(ordinary);
        if (product is null)
        {
            ClearPreviewGrid(
                ordinary
                    ? "Seleccione un producto para cargar la matriz."
                    : "No se encontró el esquema global consolidado.");
            return;
        }

        var branch = _previewBranches.SelectedItem as PreviewGroupChoice;
        var profile = _previewProfiles.SelectedItem as PreviewGroupChoice;
        var requiresOrdinaryGroups = ordinary && (branch is null || profile is null);
        var requiresSpecialBranch = special && branch is null;
        if (requiresOrdinaryGroups || requiresSpecialBranch)
        {
            ClearPreviewGrid(
                ordinary
                    ? "Seleccione un grupo de establecimientos y un grupo de perfil."
                    : "Seleccione un grupo de establecimientos.");
            return;
        }

        var sets = _preview.RateSets
            .Where(set => set.ProductId == product.Id)
            .Where(set => MatchesPreviewFilters(set, ordinary, special, branch, profile))
            .ToList();
        if (sets.Count == 0)
        {
            ClearPreviewGrid("No hay celdas configuradas para la combinación seleccionada.");
            return;
        }
        ConfigurePreviewGridColumns(ordinary);
        var rateTypes = sets.SelectMany(set => set.Rates).Select(rate => rate.RateTypeId).Distinct().Order().ToList();
        var rateTypeNames = _preview.RateTypes.ToDictionary(rateType => rateType.Id, rateType => rateType.Code);
        var headers = rateTypes.ToDictionary(
            rateTypeId => rateTypeId,
            rateTypeId => rateTypeNames.GetValueOrDefault(rateTypeId, rateTypeId.ToString()));
        if (headers.Count > 0)
        {
            var rateColumnWidth = CalculateRateColumnWidth(headers.Values);
            foreach (var rateTypeId in rateTypes)
            {
                AddPreviewColumn($"rate:{rateTypeId}", headers[rateTypeId], rateColumnWidth);
            }
        }
        var amounts = _preview.AmountRanges.ToDictionary(range => range.Id, StringComparer.Ordinal);
        var terms = _preview.TermRanges.ToDictionary(range => range.Id, StringComparer.Ordinal);
        var orderedSets = sets
            .OrderBy(set => amounts.GetValueOrDefault(set.AmountRangeId)?.Min)
            .ThenBy(set => terms.GetValueOrDefault(set.TermRangeId)?.Min);
        foreach (var set in orderedSets)
        {
            AddPreviewRow(set, product, amounts, terms);
        }

        UpdatePreviewSummary(sets, rateTypes.Count, ordinary, special, branch, profile);
    }

    private PreviewProductChoice? GetSelectedPreviewProduct(bool ordinary)
    {
        if (ordinary)
        {
            return _previewProducts.SelectedItem as PreviewProductChoice;
        }

        return _preview!.Products.FirstOrDefault() is { } globalProduct
            ? new PreviewProductChoice(globalProduct)
            : null;
    }

    private static bool MatchesPreviewFilters(
        MigrationPreviewRateSet rateSet,
        bool ordinary,
        bool special,
        PreviewGroupChoice? branch,
        PreviewGroupChoice? profile)
    {
        var matchesOrdinaryFilters = !ordinary
            || (rateSet.BranchGroupId == branch!.Id
                && rateSet.InternalRatingGroupId == profile!.Id
                && IsDefaultOrdinaryRateSet(rateSet));
        var matchesSpecialFilters = !special || rateSet.BranchGroupId == branch!.Id;

        return matchesOrdinaryFilters && matchesSpecialFilters;
    }

    private void ConfigurePreviewGridColumns(bool ordinary)
    {
        _previewGrid.Columns.Clear();
        _previewGrid.Rows.Clear();
        var productColumnHeader = ordinary ? "Producto" : "Esquema";
        var productColumnWidth = ordinary ? 130 : 160;
        AddPreviewColumn("product", productColumnHeader, productColumnWidth);

        if (_preview!.TypeSchema is "ORDINARY" or "SPECIAL")
        {
            AddPreviewColumn("amount", "Intervalos de montos", 160);
        }

        if (ordinary)
        {
            AddPreviewColumn("term", "Intervalos de plazos", 150);
        }
    }

    private int CalculateRateColumnWidth(IEnumerable<string> headers)
    {
        var headerFont = _previewGrid.ColumnHeadersDefaultCellStyle.Font ?? _previewGrid.Font;
        var widestHeader = headers.Max(header => TextRenderer.MeasureText(header, headerFont).Width + 24);

        return Math.Max(75, widestHeader);
    }

    private void AddPreviewRow(
        MigrationPreviewRateSet rateSet,
        PreviewProductChoice product,
        IReadOnlyDictionary<string, MigrationPreviewRange> amountRanges,
        IReadOnlyDictionary<string, MigrationPreviewRange> termRanges)
    {
        var row = _previewGrid.Rows[_previewGrid.Rows.Add()];
        row.Cells["product"].Value = product.Display;

        if (_previewGrid.Columns.Contains("amount"))
        {
            row.Cells["amount"].Value = PreviewInterval(
                amountRanges.GetValueOrDefault(rateSet.AmountRangeId),
                "S/ ",
                string.Empty);
        }

        if (_previewGrid.Columns.Contains("term"))
        {
            row.Cells["term"].Value = PreviewInterval(
                termRanges.GetValueOrDefault(rateSet.TermRangeId),
                string.Empty,
                " días");
        }

        foreach (var rate in rateSet.Rates)
        {
            var displayedRate = _previewValue.SelectedIndex == 1
                ? rate.CompensatoryMax
                : rate.Compensatory;
            row.Cells[$"rate:{rate.RateTypeId}"].Value = displayedRate?.ToString("0.####");
        }
    }

    private void UpdatePreviewSummary(
        IReadOnlyCollection<MigrationPreviewRateSet> rateSets,
        int rateTypeCount,
        bool ordinary,
        bool special,
        PreviewGroupChoice? branch,
        PreviewGroupChoice? profile)
    {
        var rateTypeMessage = rateTypeCount == 0
            ? "No hay tipos de tasa configurados."
            : $"Tipos de tasa: {rateTypeCount}.";
        var groupSummary = ordinary
            ? $"grupo establecimientos: {branch!.Display}; grupo perfiles: {profile!.Display}"
            : special
                ? $"grupo establecimientos: {branch!.Display}"
                : "sin filtros de grupo";
        var exceptionCount = rateSets.Sum(rateSet => rateSet.ExceptionCount);

        _previewSummary.Text =
            $"Celdas: {rateSets.Count}; {rateTypeMessage} Excepciones: {exceptionCount}; {groupSummary}";
    }

    private void ClearPreviewGrid(string message)
    {
        _previewGrid.Columns.Clear();
        _previewGrid.Rows.Clear();
        _previewSummary.Text = message;
    }

    private void AddPreviewColumn(string name, string header, int width)
    {
        _previewGrid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                Width = width,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle { Format = "N4" }
            });
    }

    private static string PreviewInterval(MigrationPreviewRange? range, string prefix, string suffix)
    {
        if (range is null)
        {
            return string.Empty;
        }

        return range.Min is null || range.Max is null
            ? range.Label
            : $"{prefix}{range.Min.Value:N2} - {prefix}{range.Max.Value:N2}{suffix}";
    }

    private async Task<bool> RunAsync(
        Func<ConfiguredPricingMigrationService, CancellationToken, Task> action,
        string operation = "operación solicitada")
    {
        if (string.IsNullOrWhiteSpace(_runId.Text))
        {
            OperationErrorPresenter.Show(
                this,
                Log,
                new ArgumentException("Ingrese un Run ID para continuar."),
                operation,
                _runId.Text.Trim());
            return false;
        }
        SetControlsEnabled(false);
        try
        {
            await action(
                new ConfiguredPricingMigrationService(CreateMigrationOptions()),
                CancellationToken.None);
            return true;
        }
        catch (Exception exception)
        {
            OperationErrorPresenter.Show(this, Log, exception, operation, _runId.Text.Trim());
            return false;
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private async Task MigrateOrPauseAsync()
    {
        if (_migrationCancellation is not null) return;

        if (string.IsNullOrWhiteSpace(_runId.Text))
        {
            OperationErrorPresenter.Show(
                this,
                Log,
                new ArgumentException("Ingrese un Run ID para iniciar la migración."),
                "iniciar migración",
                _runId.Text.Trim());
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _migrationCancellation = cancellation;
        SetControlsEnabled(false);
        _stop.Enabled = true;
        try
        {
            var service = new ConfiguredPricingMigrationService(CreateMigrationOptions());
            var result = await service.MigrateAsync(
                _runId.Text.Trim(),
                CreateProgressReporter(),
                cancellation.Token);
            Log(
                $"ETL completado: extraidas={result.Extracted}, cargadas={result.Loaded}, " +
                $"rechazadas={result.Rejected}, conjuntos={result.RateSets}.");
            await LoadRunsAsync();
        }
        catch (MigrationStoppedException exception)
        {
            _runId.Text = exception.NextRunId;
            Log(CreateMigrationStoppedMessage(exception.NextRunId));
            await LoadRunsAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Log("Migración detenida.");
        }
        catch (Exception exception)
        {
            OperationErrorPresenter.Show(this, Log, exception, "ejecutar migración", _runId.Text.Trim());
        }
        finally
        {
            _migrationCancellation = null;
            _stop.Enabled = false;
            SetControlsEnabled(true);
        }
    }

    private string CreateMigrationStoppedMessage(string nextRunId)
    {
        return SelectedTypeSchema() == "ORDINARY"
            ? "Migración detenida. El Run parcial quedó LOADED y el nuevo Run listo para los " +
                $"productos pendientes es {nextRunId}."
            : "Migración global detenida. El Run parcial quedó CANCELLED; el nuevo Run reiniciará " +
                $"todos los productos para completar el esquema global: {nextRunId}.";
    }

    private async Task TestConnectionsAsync()
    {
        await RunAsync(async (service, cancellationToken) =>
        {
            await service.TestConnectionsAsync(cancellationToken);
            Log("Conexiones validadas.");
        }, "probar conexiones");
    }

    private async Task ValidateMigrationAsync()
    {
        await RunAsync(async (service, cancellationToken) =>
        {
            var validation = await service.ValidateAsync(_runId.Text.Trim(), cancellationToken);
            Log(
                $"Validacion: {validation.Message} Filas={validation.SourceRows}, " +
                $"conjuntos={validation.RateSets}, " +
                $"duplicados={validation.DuplicateRateTypes}, " +
                $"celdas ambiguas={validation.AmbiguousCells}, " +
                $"conflictos de grupos={validation.GroupConflicts}.");
        }, "validar carga");
    }

    private async Task TransferAndPublishAsync()
    {
        await RunAsync(async (service, cancellationToken) =>
        {
            if (!ConfirmTransferAndPublish())
            {
                return;
            }

            await service.TransferAndPublishAsync(
                _runId.Text.Trim(),
                CreateProgressReporter(),
                cancellationToken);
            Log("Transferencia y publicacion completadas.");
        }, "transferir y publicar");
    }

    private bool ConfirmTransferAndPublish() =>
        MessageBox.Show(
            this,
            "Se transferira el Run validado a la base operativa y se activaran sus versiones. " +
            "Continuar?",
            "Confirmar transferencia y publicacion",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning) == DialogResult.Yes;

    private void SetControlsEnabled(bool enabled)
    {
        _sqlConnection.Enabled = enabled;
        _migrationMongoConnection.Enabled = enabled;
        _migrationMongoDatabase.Enabled = enabled;
        _targetMongoConnection.Enabled = enabled;
        _targetMongoDatabase.Enabled = enabled;
        _runId.Enabled = enabled;
        _generateRunId.Enabled = enabled;
        _runs.Enabled = enabled;
        _schemaTypeCards.Enabled = enabled;
        _productIds.Enabled = enabled && _ordinarySchema.Checked;
        _batchSize.Enabled = enabled;
        _test.Enabled = enabled;
        _migrate.Enabled = enabled;
        _validate.Enabled = enabled;
        _publish.Enabled = enabled;
        _loadRuns.Enabled = enabled;
    }

    private static IReadOnlyList<int> ParseProductIds(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<int>();
        var productIds = new List<int>();
        foreach (var part in value.Split(','))
        {
            if (!int.TryParse(part.Trim(), out var productId) || productId <= 0)
                throw new ArgumentException("idProductos debe contener enteros positivos separados por coma.");
            productIds.Add(productId);
        }
        return productIds.Distinct().Order().ToArray();
    }

    private static string NewRunId() => $"migration-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";

    private void Log(string message) => _log.AppendText($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");

    private sealed record PreviewProductChoice(MigrationPreviewProduct Product)
    {
        public string Id => Product.Id;
        public string Display => Product.Display;
    }

    private sealed record RunChoice(MigrationPreviewRun Run)
    {
        public string Id => Run.Id;
        public long RateSets => Run.RateSets;
        public string TypeSchema => Run.TypeSchema;
        public string Display =>
            $"{TypeSchemaLabel(Run.TypeSchema)} | {Run.Id} | {Run.Status} | " +
            $"conjuntos: {Run.RateSets} | {Run.CompletedAt:yyyy-MM-dd HH:mm}";
    }

    private static string TypeSchemaLabel(string typeSchema)
    {
        return typeSchema switch
        {
            "ORDINARY" => "Ordinario",
            "BASELINE" => "Línea Base",
            "SPECIAL" => "Especial",
            _ => typeSchema
        };
    }

    private sealed record PreviewGroupChoice(MigrationPreviewGroup Group)
    {
        public string Id => Group.Id;
        public string Display => string.Join(", ", Group.MemberIds.Order());
    }
}
