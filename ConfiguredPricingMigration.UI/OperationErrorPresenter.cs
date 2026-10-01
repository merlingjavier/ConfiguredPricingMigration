using System.Text.RegularExpressions;
using ConfiguredPricingMigration.Core;
using Microsoft.Data.SqlClient;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ConfiguredPricingMigration.UI;

internal static class OperationErrorPresenter
{
    public static void Show(
        IWin32Window owner,
        Action<string> log,
        Exception exception,
        string operation,
        string runId)
    {
        var incidentId = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var (title, message, advice) = Classify(exception);
        var details = CreateTechnicalDetails(incidentId, operation, runId, exception);

        log($"ERROR [{incidentId}] {operation}: {message}");

        using var dialog = new OperationErrorDialog(title, message, advice, incidentId, details);
        dialog.ShowDialog(owner);
    }

    private static string CreateTechnicalDetails(
        string incidentId,
        string operation,
        string runId,
        Exception exception)
    {
        var details = $"Incidente: {incidentId}{Environment.NewLine}" +
            $"Operación: {operation}{Environment.NewLine}" +
            $"Run ID: {runId}{Environment.NewLine}{Environment.NewLine}" +
            exception;

        return Sanitize(details);
    }

    private static (string Title, string Message, string Advice) Classify(Exception exception)
    {
        var root = RootCause(exception);
        var chainMessage = ExceptionChain(exception);
        var sql = FindException<SqlException>(exception);
        if (sql is not null)
        {
            if (IsSqlConnectionFailure(sql, chainMessage))
            {
                return (
                    "SQL Server no disponible",
                    "No se pudo establecer conexión con SQL Server.",
                    "Verifique el nombre del servidor/instancia, la VPN o red, el firewall y que " +
                    "SQL Server permita conexiones remotas. Después use “Probar conexiones”.");
            }

            if (IsSqlTimeout(sql, chainMessage))
            {
                return (
                    "Tiempo de espera agotado",
                    "SQL Server no completó la consulta dentro del tiempo permitido.",
                    "Compruebe la carga del servidor y vuelva a intentarlo. Si persiste, contacte al " +
                    "administrador de base de datos e indique el código de incidente.");
            }

            if (sql.Number == 18456 || sql.Number == 4060)
            {
                return (
                    "Acceso a SQL Server denegado",
                    "SQL Server rechazó el inicio de sesión o el acceso a la base de datos.",
                    "Verifique las credenciales y que el usuario tenga acceso a la base SQL configurada.");
            }

            return (
                "Error de SQL Server",
                "SQL Server no pudo completar la operación solicitada.",
                "Revise los detalles técnicos o compártalos con el administrador de base de datos.");
        }

        if (ContainsAny(chainMessage, "not authorized", "not authorized on"))
        {
            return (
                "Acceso a MongoDB denegado",
                "La cuenta configurada no tiene permisos para completar esta operación en MongoDB.",
                "Conceda el rol requerido sobre la base correspondiente y vuelva a intentarlo.");
        }

        var hasMongoConnectionFailure = FindException<MongoConnectionException>(exception) is not null
            || ContainsAny(chainMessage, "server selection", "connection refused", "connection timed out");
        if (hasMongoConnectionFailure)
        {
            return (
                "MongoDB no disponible",
                "No se pudo conectar con MongoDB.",
                "Verifique la URI, la red/VPN, el firewall y la disponibilidad del servidor MongoDB.");
        }

        if (root is MongoExecutionTimeoutException || root is TimeoutException)
        {
            return (
                "Tiempo de espera agotado",
                "MongoDB no completó la operación dentro del tiempo permitido.",
                "Compruebe la carga y disponibilidad de MongoDB y vuelva a intentarlo.");
        }

        if (IsDuplicateKey(exception))
        {
            return (
                "Conflicto de datos",
                "La operación generó una clave duplicada y no pudo guardarse.",
                "No repita la operación sobre el mismo borrador hasta revisar la configuración y los " +
                "datos del Run.");
        }

        if (exception is MigrationStoppedException)
        {
            return (
                "Migración detenida",
                "La migración fue detenida y su estado se guardó.",
                "Seleccione el Run pendiente creado para continuar con los productos restantes.");
        }

        if (exception is ArgumentException or FormatException)
        {
            return (
                "Datos de entrada inválidos",
                exception.Message,
                "Revise los campos indicados y vuelva a intentarlo.");
        }

        if (exception is InvalidOperationException)
        {
            return (
                "No se pudo completar la operación",
                exception.Message,
                "Revise el estado del Run y los datos requeridos antes de volver a intentarlo.");
        }

        return (
            "Error inesperado",
            "Ocurrió un error inesperado durante la operación.",
            "Puede copiar los detalles técnicos y compartirlos con soporte.");
    }

    private static bool IsSqlConnectionFailure(SqlException exception, string chainMessage)
    {
        return exception.Number is 2 or 53 or 40
            || ContainsAny(
                chainMessage,
                "network-related",
                "Error Locating Server/Instance",
                "server was not found",
                "actively refused");
    }

    private static bool IsSqlTimeout(SqlException exception, string chainMessage)
    {
        return exception.Number == -2
            || ContainsAny(chainMessage, "Execution Timeout", "timeout expired", "timed out");
    }

    private static Exception RootCause(Exception exception)
    {
        while (exception.InnerException is not null)
        {
            exception = exception.InnerException;
        }

        return exception;
    }

    private static T? FindException<T>(Exception exception) where T : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is T typed)
            {
                return typed;
            }
        }

        return null;
    }

    private static string ExceptionChain(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }

    private static bool IsDuplicateKey(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is MongoWriteException write
                && write.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                return true;
            }

            if (current is MongoBulkWriteException<BsonDocument> bulk
                && bulk.WriteErrors.Any(error => error.Category == ServerErrorCategory.DuplicateKey))
            {
                return true;
            }

            if (current.Message.Contains("E11000", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string Sanitize(string value)
    {
        value = Regex.Replace(value, @"(?i)(password|pwd)\s*=\s*[^;\r\n]*", "$1=***");
        value = Regex.Replace(value, @"(?i)(user\s*id|uid)\s*=\s*[^;\r\n]*", "$1=***");
        value = Regex.Replace(value, @"(?i)(mongodb(?:\+srv)?://)[^:/@\s]+:[^@/\s]+@", "$1***:***@");
        return value;
    }
}

internal sealed class OperationErrorDialog : Form
{
    private readonly TextBox _technicalDetails;
    private bool _detailsVisible;

    public OperationErrorDialog(string title, string message, string advice, string incidentId, string technicalDetails)
    {
        Text = title;
        Width = 660;
        Height = 285;
        MinimumSize = new Size(560, 260);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var icon = new PictureBox
        {
            Image = SystemIcons.Error.ToBitmap(),
            SizeMode = PictureBoxSizeMode.AutoSize,
            Anchor = AnchorStyles.Top,
            Margin = new Padding(0, 4, 8, 0)
        };
        layout.Controls.Add(icon, 0, 0);
        var summary = new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(550, 0),
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(0, 4, 0, 8)
        };
        layout.Controls.Add(summary, 1, 0);
        layout.Controls.Add(
            new Label
            {
                Text = advice,
                AutoSize = true,
                MaximumSize = new Size(550, 0),
                Padding = new Padding(0, 0, 0, 10)
            },
            1,
            1);

        _technicalDetails = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9),
            Text = technicalDetails,
            Visible = false
        };
        layout.Controls.Add(_technicalDetails, 0, 2);
        layout.SetColumnSpan(_technicalDetails, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft
        };
        var close = new Button
        {
            Text = "Cerrar",
            AutoSize = true,
            DialogResult = DialogResult.OK
        };
        var copy = new Button
        {
            Text = "Copiar detalles",
            AutoSize = true
        };
        var toggleDetails = new Button
        {
            Text = "Detalles técnicos",
            AutoSize = true
        };
        var incident = new Label
        {
            Text = $"Código de incidente: {incidentId}",
            AutoSize = true,
            Padding = new Padding(0, 7, 8, 0),
            ForeColor = SystemColors.GrayText
        };
        buttons.Controls.AddRange([close, copy, toggleDetails, incident]);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = close;
        CancelButton = close;

        toggleDetails.Click += (_, _) => ToggleTechnicalDetails(toggleDetails);
        copy.Click += (_, _) => CopyTechnicalDetails(technicalDetails, copy);
    }

    private void ToggleTechnicalDetails(Button toggleDetails)
    {
        _detailsVisible = !_detailsVisible;
        _technicalDetails.Visible = _detailsVisible;
        toggleDetails.Text = _detailsVisible ? "Ocultar detalles" : "Detalles técnicos";
        Height = _detailsVisible ? 520 : 285;
    }

    private void CopyTechnicalDetails(string technicalDetails, Button copyButton)
    {
        try
        {
            Clipboard.SetText(technicalDetails);
            copyButton.Text = "Copiados";
        }
        catch (Exception)
        {
            MessageBox.Show(
                this,
                "No se pudo copiar al portapapeles. Abra los detalles técnicos y cópielos manualmente.",
                "Portapapeles no disponible",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }
}
