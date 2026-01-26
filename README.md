I've need app like this to use old Windows 8 tablet as home-print-server to share legacy HP printer with all devices in home. The problem was fact, that HP has never released driver for ARM CPU's for this printer.

So I've got an idea to create simple web application/service, when I'll be able to just send files and click "print" from my phone, and this app will receive those files, render it (because .pdf's can't be easly sent to printer, like images) and print them by USB just as local connected printer with driver for x86 32-bit CPU.

To resolve problems for those old-legacy Windows printers, like stucking and blocking documents in printer queue, this app in UI has got also 2 features to quickly fix them: first one is force stopping spooler service, deleting all files from spooler buffer system catalog and starts spooler again.
The second option does everything as the first one, but also after that, it will completly reboot machine. After few minutes service will auto start with Windows, even without logging any user.


[![screenshot](https://raw.githubusercontent.com/miclat97/WinPrintBridge/refs/heads/main/WinPrintBridgeUI.png)
