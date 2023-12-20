using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System;
using Windows.Management.Deployment;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using MsBox.Avalonia.Models;
using MsBox.Avalonia;

namespace FirefoxNightlyMSIXUpdater
{
    public partial class MainWindow : Window
    {
        private const string DownloadDirectoryPageUrl = "https://ftp.mozilla.org/pub/firefox/nightly/latest-mozilla-central/";
        private const string FirefoxPackageFamilyName = "Mozilla.MozillaFirefoxNightly_gmpnhwe7bv608";

        [GeneratedRegex("(firefox-.*win64.installer.msix)\".*\\n.*\\n.*.*<td>(\\d+-\\w+-\\d+\\s\\d+:\\d+)")]
        private static partial Regex DownloadItemMatchRegex();

        [GeneratedRegex("firefox-(\\d+)")]
        private static partial Regex DownloadItemVersionMatchRegex();


        private string latestDownloadAddress = "";
        private string downloadTempFilePath = "";
        private Thread? downloadInstallThread;
        private bool installingPackage;
        private bool applicationExiting;


        public MainWindow()
        {
            this.InitializeComponent();

            this.SetLoadingMaskVisibility(true).GetAwaiter();
            this.Closing += this.WindowClosing;
        }

        private void WindowLoaded(object sender, EventArgs e)
        {
            this.UpdatingInformation();
        }

        private void UpdateButtonClick(object sender, RoutedEventArgs e)
        {
            this.UpdateButton.IsEnabled = false;

            this.downloadInstallThread = new Thread(async () =>
            {
                try
                {
                    this.DownloadAndInstall().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    await this.DisplayMessage($"Update failed: {this.GetRealErrorMessage(ex)}", "Error",
                        MsBox.Avalonia.Enums.Icon.Error);
                }
                finally
                {
                    await Dispatcher.UIThread.InvokeAsync(() => this.UpdateButton.IsEnabled = true);
                }
            });

            this.downloadInstallThread.Start();
        }

        private void CloseButtonClick(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void WindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;

            var cleanupThread = new Thread(this.QuitCleanup);
            cleanupThread.Start();
        }

        private async void UpdatingInformation()
        {
            var checkLocalThread = new Thread(this.CheckingLocalPackageVersion);

            await Task.WhenAll(
                Task.Run(() =>
                {
                    checkLocalThread.Start();
                    checkLocalThread.Join();
                }),
                this.CheckingOnlineVersion()
            );

            await this.SetLoadingMaskVisibility(false);
        }

        private async void CheckingLocalPackageVersion()
        {
            var pm = new PackageManager();
            var availablePackages = pm.FindPackagesForUser(string.Empty, FirefoxPackageFamilyName).ToArray();

            if (!availablePackages.Any())
            {
                await Dispatcher.UIThread.InvokeAsync(() => { this.InstalledVersion.Content = "Not installed"; });
                await this.DisplayMessage("Firefox Nightly seems not installed on this device", "Error",
                    MsBox.Avalonia.Enums.Icon.Error);

                return;
            }

            var p = availablePackages.First();
            var v = p.Id.Version;
            var versionString = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";

            Debug.WriteLine($"Local installed version {versionString}");

            await Dispatcher.UIThread.InvokeAsync(() => { this.InstalledVersion.Content = versionString; });
        }

        private async Task CheckingOnlineVersion()
        {
            string response;

            try
            {
                using var hc = new HttpClient();
                response = await hc.GetStringAsync(DownloadDirectoryPageUrl);
            }
            catch (Exception ex)
            {
                await this.DisplayMessage($"Unable obtain latest version info: {this.GetRealErrorMessage(ex)}", "Error",
                    MsBox.Avalonia.Enums.Icon.Error);

                Environment.Exit(1);
                return;
            }

            var nameWithTimeRegex = DownloadItemMatchRegex();
#if NET
            var matchResult = nameWithTimeRegex.Matches(response).ToArray().Last();
#else
            var matchResult = nameWithTimeRegex.Matches(response).Cast<Match>().ToArray().Last();
#endif

            var downloadAddress = $"{DownloadDirectoryPageUrl}{matchResult.Groups[1]}";

            var versionRegex = DownloadItemVersionMatchRegex();
            var version = versionRegex.Match(downloadAddress).Groups[1].ToString();
            var installerTime = DateTime.Parse(matchResult.Groups[2].ToString()).ToString("yyyy-MM-dd HH:mm");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                this.OnlineVersion.Content = version;
                this.OnlineInstallerDate.Content = $"({installerTime})";
            });

            Debug.WriteLine($"Latest version: {version}({installerTime})");
            Debug.WriteLine($"Latest download address: {downloadAddress}");
            this.latestDownloadAddress = downloadAddress;
        }

        private async Task DownloadAndInstall()
        {
            this.downloadTempFilePath = Path.GetTempFileName();

            Debug.WriteLine($"Downloading to {this.downloadTempFilePath}");

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(5);

                try
                {
                    using var response = await client.GetAsync(this.latestDownloadAddress, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    await using var contentStream = await response.Content.ReadAsStreamAsync();
                    await using var fileStream = new FileStream(this.downloadTempFilePath, FileMode.Create, FileAccess.Write,
                        FileShare.None, 4096, true);

                    var responseByteSize = response.Content.Headers.ContentLength ?? 0;
                    var totalRead = 0L;
                    var totalReads = 0L;
                    var buffer = new byte[4096];
                    var isMoreToRead = true;

                    do
                    {
                        if (this.applicationExiting)
                        {
                            contentStream.Close();
                            fileStream.Close();

                            return;
                        }

                        var readCount = await contentStream.ReadAsync(buffer, 0, buffer.Length);

                        if (readCount == 0)
                        {
                            isMoreToRead = false;
                        }
                        else
                        {
                            await fileStream.WriteAsync(buffer, 0, readCount);

                            totalRead += readCount;
                            totalReads += 1;

                            if (totalReads % 100 != 0) continue;

                            var percent = (Convert.ToDecimal(totalRead) / responseByteSize) * 100;
                            await Dispatcher.UIThread.InvokeAsync(() => this.DownloadProgress.Value = (double)percent);
                        }
                    } while (isMoreToRead);
                }
                catch (Exception ex)
                {
                    this.ResetDownloadProgressAndCleanTemp();

                    await this.DisplayMessage($"Error while downloading installer: {this.GetRealErrorMessage(ex)}",
                        "Error",
                        MsBox.Avalonia.Enums.Icon.Error);

                    return;
                }
            }

            this.installingPackage = true;
            await Dispatcher.UIThread.InvokeAsync(() => this.DownloadProgress.IsIndeterminate = true);

            var pm = new PackageManager();

            try
            {
                pm.AddPackageAsync(new Uri(this.downloadTempFilePath), Array.Empty<Uri>(),
                        DeploymentOptions.ForceApplicationShutdown)
                    .GetAwaiter().GetResult();
                // FIXME:
                // .NET 6: await keyword hang while upgrade from older version
                // https://github.com/dotnet/wpf/issues/4097
                // await pm.AddPackageAsync(new Uri(this.downloadTempFilePath), Array.Empty<Uri>(), DeploymentOptions.ForceApplicationShutdown);
            }
            catch (Exception ex)
            {
                this.ResetDownloadProgressAndCleanTemp();

                await this.DisplayMessage(
                    $"Error while install package: {this.GetRealErrorMessage(ex)}", "Error",
                    MsBox.Avalonia.Enums.Icon.Error);

                return;
            }
            finally
            {
                this.installingPackage = false;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    this.DownloadProgress.IsIndeterminate = false;
                    this.DownloadProgress.Value = 0;
                });
            }

            try
            {
                File.Delete(this.downloadTempFilePath);
            }
            catch (Exception ex)
            {
                await this.DisplayMessage($"Error while delete installer file: {ex.Message}", "Error",
                    MsBox.Avalonia.Enums.Icon.Error);
            }


            await this.SetUpToDateMaskVisibility(true);
        }

        private async void ResetDownloadProgressAndCleanTemp()
        {
            try
            {
                File.Delete(this.downloadTempFilePath);
            }
            catch (Exception ex)
            {
                Debug.Write($"Unable to delete temp file: {ex.Message}");
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    this.DownloadProgress.IsIndeterminate = false;
                    this.DownloadProgress.Value = 0;
                });
            }
        }

        private async void QuitCleanup()
        {
            if (this.downloadInstallThread != null && this.downloadInstallThread.ThreadState != System.Threading.ThreadState.Stopped)
            {
                this.applicationExiting = true;

                if (this.installingPackage)
                {
                    await this.SetLoadingMaskVisibility(true, "Waiting for installation finish");
                }

                while (this.downloadInstallThread.ThreadState != System.Threading.ThreadState.Stopped)
                {
                    Debug.WriteLine($"Waiting download install thread to exit {this.downloadInstallThread.ThreadState}");
                    Thread.Sleep(100);
                }

                try
                {
                    File.Delete(this.downloadTempFilePath);
                    Debug.WriteLine($"Deleted installer {this.downloadTempFilePath}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message);
                }
                finally
                {
                    Environment.Exit(0);
                }
            }
            else
            {
                Environment.Exit(0);
            }
        }

        #region UI

        private async Task DisplayMessage(string message, string title, MsBox.Avalonia.Enums.Icon icon)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var messageBoxStandardWindow = MessageBoxManager.GetMessageBoxCustom(
                    new MsBox.Avalonia.Dto.MessageBoxCustomParams
                    {
                        WindowIcon = this.Icon,
                        ContentTitle = title,
                        ContentMessage = message,
                        ButtonDefinitions = new ButtonDefinition[]
                        {
                            new() { Name = "Ok" }
                        },
                        Icon = icon,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        FontFamily = "Segoe UI, Microsoft YaHei UI"
                    });

                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    return messageBoxStandardWindow.ShowWindowDialogAsync(desktop.MainWindow);
                }
                else
                {
                    return messageBoxStandardWindow.ShowAsync();
                }
            });
        }

        private async Task SetLoadingMaskVisibility(bool visible, string? content = null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (content is not null)
                {
                    this.LoadingText.Content = content;
                }

                this.LoadingMask.IsVisible = visible;
                this.ContentGrid.IsVisible = !visible;
            });
        }

        private async Task SetUpToDateMaskVisibility(bool visible)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                this.UpToDateMask.IsVisible = visible;
                this.ContentGrid.IsVisible = !visible;
                this.LoadingMask.IsVisible = false;
            });
        }

        #endregion

        #region Utils

        private string GetRealErrorMessage(Exception ex)
        {
            // HTTP request does not contain any useful information in its outer exception
            if (ex.Message.Contains("inner exception") && ex.InnerException != null)
            {
                return ex.InnerException.Message;
            }
            else
            {
                return ex.Message;
            }
        }

        #endregion
    }
}