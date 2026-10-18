using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZBSWindowsComponent
{
    public class SettingsForm : Form
    {
        private readonly ToolTip _toolTip = new ToolTip();

        private string _configDir;
        private string _launcherPath;
        private JObject _settingsJson;
        private JObject _modelsJson;
        private string _currentContextName;
        private bool _loadingContext;

        private TextBox _hotkeyTextBox;
        private CheckBox _backgroundCheckBox;
        private CheckBox _autostartCheckBox;
        private ComboBox _contextComboBox;
        private TextBox _modelTextBox;
        private NumericUpDown _temperatureNumeric;
        private NumericUpDown _maxTokensNumeric;
        private Button _openConfigButton;
        private Button _saveButton;

        public SettingsForm()
        {
            InitializeComponent();
            LoadConfiguration();
        }

        private void InitializeComponent()
        {
            DpiAwareness.ConfigureForm(this);
            Text = "Настройки Make it ZBS";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(620, 420);
            Font = SystemFonts.MessageBoxFont;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = 4
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(CreateGeneralGroup(), 0, 0);
            root.Controls.Add(CreateModelsGroup(), 0, 1);
            root.Controls.Add(CreateConfigToolsPanel(), 0, 2);
            root.Controls.Add(CreateButtonsPanel(), 0, 3);

            Controls.Add(root);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.TryEnableDarkTitleBar(Handle);
        }

        private Control CreateGeneralGroup()
        {
            var group = new GroupBox
            {
                Text = "Общие настройки",
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(10)
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _hotkeyTextBox = new TextBox { Dock = DockStyle.Fill };
            _backgroundCheckBox = new CheckBox
            {
                Text = "Фоновый режим — скрывать консоль",
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            _autostartCheckBox = new CheckBox
            {
                Text = "Запускать при старте Windows",
                AutoSize = true,
                Dock = DockStyle.Fill
            };

            layout.Controls.Add(new Label
            {
                Text = "Горячая клавиша",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 8, 6)
            }, 0, 0);
            layout.Controls.Add(_hotkeyTextBox, 1, 0);
            layout.Controls.Add(_backgroundCheckBox, 0, 1);
            layout.SetColumnSpan(_backgroundCheckBox, 2);
            layout.Controls.Add(_autostartCheckBox, 0, 2);
            layout.SetColumnSpan(_autostartCheckBox, 2);

            group.Controls.Add(layout);
            return group;
        }

        private Control CreateModelsGroup()
        {
            var group = new GroupBox
            {
                Text = "Модели",
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(10)
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _contextComboBox = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _contextComboBox.SelectedIndexChanged += ContextComboBox_SelectedIndexChanged;

            _modelTextBox = new TextBox { Dock = DockStyle.Fill };
            _temperatureNumeric = new NumericUpDown
            {
                DecimalPlaces = 2,
                Increment = 0.1m,
                Minimum = 0,
                Maximum = 2,
                Dock = DockStyle.Left,
                Width = 120
            };
            _maxTokensNumeric = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 1000000,
                Increment = 128,
                Dock = DockStyle.Left,
                Width = 120
            };

            AddLabeledControl(layout, "Контекст", _contextComboBox, 0);
            AddLabeledControl(layout, "Model", _modelTextBox, 1);
            AddLabeledControl(layout, "Temperature", _temperatureNumeric, 2);
            AddLabeledControl(layout, "Max tokens", _maxTokensNumeric, 3);

            group.Controls.Add(layout);
            return group;
        }

        private Control CreateConfigToolsPanel()
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 10, 0, 0)
            };

            _openConfigButton = new Button
            {
                Text = "Открыть папку конфигов",
                AutoSize = true
            };
            _openConfigButton.Click += OpenConfigButton_Click;

            panel.Controls.Add(_openConfigButton);
            return panel;
        }

        private Control CreateButtonsPanel()
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Padding = new Padding(0, 10, 0, 0)
            };

            var cancelButton = new Button
            {
                Text = "Отмена",
                DialogResult = DialogResult.Cancel,
                Width = 100
            };

            _saveButton = new Button
            {
                Text = "Сохранить",
                Width = 100
            };
            _saveButton.Click += SaveButton_Click;

            panel.Controls.Add(cancelButton);
            panel.Controls.Add(_saveButton);

            AcceptButton = _saveButton;
            CancelButton = cancelButton;

            return panel;
        }

        private static void AddLabeledControl(TableLayoutPanel layout, string labelText, Control control, int row)
        {
            layout.Controls.Add(new Label
            {
                Text = labelText,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 8, 6)
            }, 0, row);
            layout.Controls.Add(control, 1, row);
        }

        private void LoadConfiguration()
        {
            _configDir = ProjectPaths.ConfigDir;
            _launcherPath = ProjectPaths.LauncherPath;

            if (string.IsNullOrEmpty(_configDir))
            {
                SetEditorEnabled(false);
                _openConfigButton.Enabled = false;
                _toolTip.SetToolTip(_openConfigButton, "каталог config не найден");
                MessageBox.Show(this, "Каталог config не найден.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var settingsPath = Path.Combine(_configDir, "settings.json");
            var modelsPath = Path.Combine(_configDir, "models.json");

            try
            {
                _settingsJson = SettingsStore.LoadJObject(settingsPath);
                _modelsJson = SettingsStore.LoadJObject(modelsPath);
                LoadSettingsFields();
                LoadModelContexts();
                LoadAutostartState();
            }
            catch (Exception ex)
            {
                SetEditorEnabled(false);
                MessageBox.Show(this, "Не удалось загрузить настройки: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadSettingsFields()
        {
            var general = GetOrCreateObject(_settingsJson, "general");
            _hotkeyTextBox.Text = ReadString(general, "hotkey", string.Empty);
            _backgroundCheckBox.Checked = ReadBool(general, "background", true);
        }

        private void LoadModelContexts()
        {
            var contexts = _modelsJson["contexts"] as JObject;
            _contextComboBox.BeginUpdate();
            try
            {
                _contextComboBox.Items.Clear();

                if (contexts != null)
                {
                    foreach (var property in contexts.Properties())
                    {
                        _contextComboBox.Items.Add(property.Name);
                    }
                }
            }
            finally
            {
                _contextComboBox.EndUpdate();
            }

            var hasContexts = _contextComboBox.Items.Count > 0;
            SetModelFieldsEnabled(hasContexts);

            if (hasContexts)
            {
                _contextComboBox.SelectedIndex = 0;
            }
        }

        private void LoadAutostartState()
        {
            try
            {
                _autostartCheckBox.Checked = SettingsStore.IsAutostartEnabled();
            }
            catch (Exception ex)
            {
                _autostartCheckBox.Checked = false;
                _autostartCheckBox.Enabled = false;
                _toolTip.SetToolTip(_autostartCheckBox, "не удалось прочитать автозапуск: " + ex.Message);
                return;
            }

            if (string.IsNullOrEmpty(_launcherPath))
            {
                _autostartCheckBox.Enabled = false;
                _toolTip.SetToolTip(_autostartCheckBox, "лаунчер не найден");
            }
            else
            {
                _autostartCheckBox.Enabled = true;
                _toolTip.SetToolTip(_autostartCheckBox, _launcherPath);
            }
        }

        private void ContextComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            SaveCurrentContextToMemory();

            _currentContextName = _contextComboBox.SelectedItem as string;
            LoadCurrentContextFields();
        }

        private void LoadCurrentContextFields()
        {
            JObject context;
            if (!TryGetContextObject(_modelsJson, _currentContextName, out context))
            {
                SetModelFieldsEnabled(false);
                return;
            }

            _loadingContext = true;
            try
            {
                SetModelFieldsEnabled(true);
                _modelTextBox.Text = ReadString(context, "model", string.Empty);
                SetNumericValue(_temperatureNumeric, ReadDecimal(context, "temperature", 0.5m));
                SetNumericValue(_maxTokensNumeric, ReadDecimal(context, "maxTokens", 2048m));
            }
            finally
            {
                _loadingContext = false;
            }
        }

        private void SaveCurrentContextToMemory()
        {
            if (_loadingContext || string.IsNullOrEmpty(_currentContextName))
            {
                return;
            }

            JObject context;
            if (!TryGetContextObject(_modelsJson, _currentContextName, out context))
            {
                return;
            }

            context["model"] = _modelTextBox.Text.Trim();
            context["temperature"] = _temperatureNumeric.Value;
            context["maxTokens"] = decimal.ToInt32(_maxTokensNumeric.Value);
        }

        internal static bool TryGetContextObject(
            JObject modelsJson, string currentContextName, out JObject context)
        {
            context = null;
            if (string.IsNullOrEmpty(currentContextName) || modelsJson == null)
            {
                return false;
            }

            var contexts = modelsJson["contexts"] as JObject;
            if (contexts == null)
            {
                return false;
            }

            context = contexts[currentContextName] as JObject;
            return context != null;
        }

        private void SaveButton_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_hotkeyTextBox.Text))
            {
                MessageBox.Show(this, "Горячая клавиша не может быть пустой.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _hotkeyTextBox.Focus();
                return;
            }

            try
            {
                SaveCurrentContextToMemory();
                ApplySettingsFields();

                var warning = SettingsSaveWorkflow.Commit(
                    () => ConfigurationStore.Save(_configDir, new System.Collections.Generic.Dictionary<string, JObject>
                    {
                        ["settings.json"] = _settingsJson,
                        ["models.json"] = _modelsJson
                    }), SaveAutostartState, SettingsState.MarkDirty);
                if (warning != null)
                    MessageBox.Show(this, UiText.Current(
                        "Настройки сохранены и применены, но изменить автозапуск не удалось: ",
                        "Settings were saved and applied, but Windows startup could not be changed: ") + warning.Message,
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось сохранить настройки: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ApplySettingsFields()
        {
            var general = GetOrCreateObject(_settingsJson, "general");
            general["hotkey"] = _hotkeyTextBox.Text.Trim();
            general["background"] = _backgroundCheckBox.Checked;
        }

        private void SaveAutostartState()
        {
            if (!_autostartCheckBox.Enabled)
            {
                return;
            }

            SettingsStore.SetAutostart(_autostartCheckBox.Checked, _launcherPath);
        }

        private void OpenConfigButton_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_configDir))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _configDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось открыть папку конфигов: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SetEditorEnabled(bool enabled)
        {
            _hotkeyTextBox.Enabled = enabled;
            _backgroundCheckBox.Enabled = enabled;
            _autostartCheckBox.Enabled = enabled;
            _contextComboBox.Enabled = enabled;
            _modelTextBox.Enabled = enabled;
            _temperatureNumeric.Enabled = enabled;
            _maxTokensNumeric.Enabled = enabled;
            _saveButton.Enabled = enabled;
        }

        private void SetModelFieldsEnabled(bool enabled)
        {
            _contextComboBox.Enabled = enabled;
            _modelTextBox.Enabled = enabled;
            _temperatureNumeric.Enabled = enabled;
            _maxTokensNumeric.Enabled = enabled;
        }

        private static JObject GetOrCreateObject(JObject parent, string propertyName)
        {
            var value = parent[propertyName] as JObject;
            if (value == null)
            {
                value = new JObject();
                parent[propertyName] = value;
            }

            return value;
        }

        private static string ReadString(JObject obj, string propertyName, string defaultValue)
        {
            var token = obj[propertyName];
            return token == null || token.Type == JTokenType.Null ? defaultValue : token.ToString();
        }

        private static bool ReadBool(JObject obj, string propertyName, bool defaultValue)
        {
            var token = obj[propertyName];
            if (token == null || token.Type == JTokenType.Null)
            {
                return defaultValue;
            }

            try
            {
                return token.Value<bool>();
            }
            catch (FormatException)
            {
                return defaultValue;
            }
            catch (InvalidCastException)
            {
                return defaultValue;
            }
        }

        private static decimal ReadDecimal(JObject obj, string propertyName, decimal defaultValue)
        {
            var token = obj[propertyName];
            if (token == null || token.Type == JTokenType.Null)
            {
                return defaultValue;
            }

            try
            {
                return token.Value<decimal>();
            }
            catch (FormatException)
            {
                return defaultValue;
            }
            catch (InvalidCastException)
            {
                return defaultValue;
            }
        }

        private static void SetNumericValue(NumericUpDown control, decimal value)
        {
            if (value < control.Minimum)
            {
                value = control.Minimum;
            }
            else if (value > control.Maximum)
            {
                value = control.Maximum;
            }

            control.Value = value;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _toolTip.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
