Prusa Connect Widget - sideload installer

What's in this folder:
  install.ps1   one-line installer (trusts the cert, installs the package)
  *.cer         the self-signed dev certificate this build was signed with
  *.msix        the app package

Install:
  Right-click install.ps1 and Run with PowerShell (it needs admin to trust the
  cert), or from an elevated PowerShell prompt:
    powershell -ExecutionPolicy Bypass -File .\install.ps1

Then open the Widgets Board (Win+W), add a "Prusa Connect - 1..4" tile, and set
each tile (printer / team / farm status / farm orders) in the Prusa Connect
Widget app from the Start menu.

The certificate is a self-signed development cert. Trusting it is what lets
Windows install a package that isn't signed by a public CA. Uninstall any time:
Start menu, right-click Prusa Connect Widget, Uninstall.
