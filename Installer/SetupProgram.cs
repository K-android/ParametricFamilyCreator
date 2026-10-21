using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace ParametricFamilyForge.Setup
{
    public class SetupForm : Form
    {
        private CheckedListBox chkListVersions;
        private Button btnInstall;
        private Button btnUninstall;
        private Label lblStatus;
        private ProgressBar progressBar;
        private TextBox txtLog;

        public SetupForm()
        {
            InitializeComponent();
            DetectRevitVersions();
        }

        private void InitializeComponent()
        {
            this.Text = "ParametricFamilyForge — Setup Wizard";
            this.Size = new Size(540, 480);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.BackColor = Color.FromArgb(24, 26, 31);
            this.ForeColor = Color.FromArgb(224, 226, 232);
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            // Header Banner
            Panel pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 70,
                BackColor = Color.FromArgb(33, 37, 43),
                Padding = new Padding(16, 12, 16, 12)
            };

            Label lblTitle = new Label
            {
                Text = "Parametric Family Forge Setup",
                Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(97, 175, 239),
                AutoSize = true,
                Location = new Point(14, 10)
            };

            Label lblSubtitle = new Label
            {
                Text = "Autodesk Revit 2024, 2025 & 2026 AI Parametric Add-in",
                Font = new Font("Segoe UI", 9.0f, FontStyle.Regular),
                ForeColor = Color.FromArgb(171, 178, 191),
                AutoSize = true,
                Location = new Point(16, 38)
            };

            pnlHeader.Controls.Add(lblTitle);
            pnlHeader.Controls.Add(lblSubtitle);
            this.Controls.Add(pnlHeader);

            // Instructions
            Label lblSelect = new Label
            {
                Text = "Select the Autodesk Revit versions to install the add-in for:",
                Location = new Point(18, 85),
                AutoSize = true,
                ForeColor = Color.FromArgb(152, 195, 121)
            };
            this.Controls.Add(lblSelect);

            // CheckedListBox
            chkListVersions = new CheckedListBox
            {
                Location = new Point(18, 110),
                Size = new Size(488, 80),
                BackColor = Color.FromArgb(40, 44, 52),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                CheckOnClick = true
            };
            this.Controls.Add(chkListVersions);

            // Log / Status TextBox
            txtLog = new TextBox
            {
                Location = new Point(18, 200),
                Size = new Size(488, 140),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(30, 34, 40),
                ForeColor = Color.FromArgb(171, 178, 191),
                Font = new Font("Consolas", 8.5f),
                BorderStyle = BorderStyle.FixedSingle
            };
            this.Controls.Add(txtLog);

            // Progress Bar
            progressBar = new ProgressBar
            {
                Location = new Point(18, 350),
                Size = new Size(488, 10),
                Visible = false
            };
            this.Controls.Add(progressBar);

            // Status Label
            lblStatus = new Label
            {
                Text = "Ready to install. No admin rights required.",
                Location = new Point(18, 368),
                AutoSize = true,
                ForeColor = Color.FromArgb(92, 99, 112),
                Font = new Font("Segoe UI", 8.5f)
            };
            this.Controls.Add(lblStatus);

            // Buttons
            btnInstall = new Button
            {
                Text = "Install Add-in",
                Location = new Point(366, 395),
                Size = new Size(140, 34),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnInstall.FlatAppearance.BorderSize = 0;
            btnInstall.Click += BtnInstall_Click;
            this.Controls.Add(btnInstall);

            btnUninstall = new Button
            {
                Text = "Uninstall",
                Location = new Point(256, 395),
                Size = new Size(100, 34),
                BackColor = Color.FromArgb(62, 68, 81),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.0f),
                Cursor = Cursors.Hand
            };
            btnUninstall.FlatAppearance.BorderSize = 0;
            btnUninstall.Click += BtnUninstall_Click;
            this.Controls.Add(btnUninstall);
        }

        private void DetectRevitVersions()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] years = new string[] { "2026", "2025", "2024" };

            foreach (string y in years)
            {
                string revitDir = @"C:\Program Files\Autodesk\Revit " + y;
                string addinDir = Path.Combine(appData, "Autodesk", "Revit", "Addins", y);
                bool detected = Directory.Exists(revitDir) || Directory.Exists(addinDir);

                int idx = chkListVersions.Items.Add("Autodesk Revit " + y + (detected ? " (Detected)" : ""));
                chkListVersions.SetItemChecked(idx, true);
            }

            Log("Setup wizard initialized.");
            Log("Targeting standard user directory: %APPDATA%\\Autodesk\\Revit\\Addins\\");
        }

        private void Log(string msg)
        {
            txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine);
        }

        private void BtnInstall_Click(object sender, EventArgs e)
        {
            btnInstall.Enabled = false;
            btnUninstall.Enabled = false;
            progressBar.Visible = true;
            progressBar.Style = ProgressBarStyle.Marquee;

            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string baseAddinsDir = Path.Combine(appData, "Autodesk", "Revit", "Addins");
                string currentDir = AppDomain.CurrentDomain.BaseDirectory;

                int installedCount = 0;

                for (int i = 0; i < chkListVersions.Items.Count; i++)
                {
                    if (chkListVersions.GetItemChecked(i))
                    {
                        string itemText = chkListVersions.Items[i].ToString();
                        string year = "2025";
                        if (itemText.Contains("2024")) year = "2024";
                        else if (itemText.Contains("2025")) year = "2025";
                        else if (itemText.Contains("2026")) year = "2026";

                        Log("Deploying for Revit " + year + "...");

                        string yearAddinDir = Path.Combine(baseAddinsDir, year);
                        if (!Directory.Exists(yearAddinDir)) Directory.CreateDirectory(yearAddinDir);

                        string packageDir = Path.Combine(yearAddinDir, "ParametricFamilyForge");
                        if (!Directory.Exists(packageDir)) Directory.CreateDirectory(packageDir);

                        // Locate pre-compiled binaries
                        string sourceBinDir = (year == "2024")
                            ? Path.Combine(currentDir, "Release_Binaries", "Revit_2024")
                            : Path.Combine(currentDir, "Release_Binaries", "Revit_2025_2026");

                        if (!Directory.Exists(sourceBinDir))
                        {
                            string tfm = (year == "2024") ? "net48" : "net8.0-windows";
                            sourceBinDir = Path.Combine(currentDir, "bin", "Release", tfm);
                        }
                        if (!Directory.Exists(sourceBinDir)) sourceBinDir = currentDir;

                        string installedDllPath = Path.Combine(packageDir, "ParametricFamilyForge.dll");

                        // Copy all DLLs found in source directory
                        if (Directory.Exists(sourceBinDir))
                        {
                            foreach (string dll in Directory.GetFiles(sourceBinDir, "*.dll"))
                            {
                                string dest = Path.Combine(packageDir, Path.GetFileName(dll));
                                File.Copy(dll, dest, true);
                            }
                        }

                        // Generate Add-in Manifest
                        string manifestPath = Path.Combine(yearAddinDir, "ParametricFamilyForge.addin");
                        string manifestXml = @"<?xml version=""1.0"" encoding=""utf-8"" standalone=""no""?>
<RevitAddIns>
  <AddIn Type=""Application"">
    <Name>ParametricFamilyForge</Name>
    <Assembly>" + installedDllPath + @"</Assembly>
    <AddInId>8B145E9F-349C-4D2A-9447-0F89E88E4B17</AddInId>
    <FullClassName>ParametricFamilyForge.App</FullClassName>
    <VendorId>AECFORGE</VendorId>
    <VendorDescription>Parametric Family Forge - AI-Driven Revit Family Generator</VendorDescription>
  </AddIn>
</RevitAddIns>";

                        File.WriteAllText(manifestPath, manifestXml, Encoding.UTF8);
                        Log("Installed manifest: " + manifestPath);
                        installedCount++;
                    }
                }

                // Initialize Configuration File
                string configDir = Path.Combine(baseAddinsDir, "ParametricFamilyForge");
                if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
                string configFile = Path.Combine(configDir, "config.json");

                if (!File.Exists(configFile))
                {
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop).Replace("\\", "\\\\");
                    string defaultConfig = @"{
  ""api_key"": """",
  ""model_engine"": ""gemini-3.8"",
  ""temperature"": 0.2,
  ""template_directory"": ""C:\\ProgramData\\Autodesk\\RVT 2025\\Family Templates\\English"",
  ""output_directory"": """ + desktop + @"\\\\RevitGeneratedFamilies"",
  ""auto_load_into_project"": true
}";
                    File.WriteAllText(configFile, defaultConfig, Encoding.UTF8);
                    Log("Initialized configuration: " + configFile);
                }

                progressBar.Visible = false;
                lblStatus.Text = "Installation completed successfully!";
                lblStatus.ForeColor = Color.FromArgb(152, 195, 121);
                Log("Installation successful for " + installedCount + " Revit version(s)!");

                MessageBox.Show(this, 
                    "Parametric Family Forge has been successfully installed!\n\nOpen Autodesk Revit and look for the 'AI Family Forge' tab on your ribbon.", 
                    "Setup Complete", 
                    MessageBoxButtons.OK, 
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                progressBar.Visible = false;
                lblStatus.Text = "Installation encountered an error.";
                lblStatus.ForeColor = Color.FromArgb(224, 108, 117);
                Log("ERROR: " + ex.Message);
                MessageBox.Show(this, "Installation Error:\n" + ex.Message, "Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnInstall.Enabled = true;
                btnUninstall.Enabled = true;
            }
        }

        private void BtnUninstall_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(this, "Are you sure you want to uninstall Parametric Family Forge from Revit?", "Confirm Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string baseAddinsDir = Path.Combine(appData, "Autodesk", "Revit", "Addins");
                string[] years = new string[] { "2024", "2025", "2026" };

                int removed = 0;
                foreach (string y in years)
                {
                    string mPath = Path.Combine(baseAddinsDir, y, "ParametricFamilyForge.addin");
                    string pDir = Path.Combine(baseAddinsDir, y, "ParametricFamilyForge");

                    if (File.Exists(mPath)) { File.Delete(mPath); Log("Removed: " + mPath); removed++; }
                    if (Directory.Exists(pDir)) { Directory.Delete(pDir, true); Log("Removed: " + pDir); removed++; }
                }

                lblStatus.Text = "Add-in uninstalled.";
                Log("Uninstall complete.");
                MessageBox.Show(this, "Parametric Family Forge has been uninstalled.", "Uninstall Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("ERROR during uninstall: " + ex.Message);
                MessageBox.Show(this, "Uninstall Error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }
    }
}
