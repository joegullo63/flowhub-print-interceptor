# Flowhub Maui desktop integration notes

These observations apply to the locally inspected Flowhub Maui 3.2.12 Windows installation. Flowhub can change them in any update.

## What is installed locally

The Windows client is an Electron wrapper around `https://app.flowhub.com`. Its application package is stored under the installed version's `resources/app.asar`. The wrapper contains readable JavaScript for window creation, printer menus, local configuration, printing, and updates.

`printFiles.zip` contains the licensed PDF printing utility and test assets such as `test-receipt.pdf` and `test-fulfillment.pdf`. Those PDFs only drive Flowhub's test-print menu entries.

## How production printing works

The hosted Flowhub application generates each live PDF and sends its bytes plus a print type to the Electron preload bridge. The desktop wrapper then:

1. writes the source to Flowhub's user-data `print-util/printFiles` directory;
2. names it with an internal identifier and type, such as `-receipt.pdf` or `-fulfillment.pdf`;
3. invokes its bundled PDF-to-Windows-printer utility with the selected queue;
4. deletes the production source PDF later.

That boundary is why Print Interceptor can classify jobs reliably without modifying Flowhub.

## Can the default receipt appearance be changed locally?

Not by replacing `test-receipt.pdf`. Production receipt layout comes from the hosted Flowhub web application before the desktop wrapper receives it.

It is technically possible to repack `app.asar` and patch the desktop printing code to post-process live PDFs with the bundled `pdf-lib` dependency. That approach is unsupported and fragile:

- Flowhub/Squirrel updates replace the modified package;
- changing an authenticated POS wrapper increases operational and compliance risk;
- PDF transformations can break barcodes, pagination, tax text, or required receipt content;
- support may decline to troubleshoot a modified client.

A separate, versioned post-processing feature in Print Interceptor would be more maintainable than patching Flowhub, but it should be designed and tested as its own project scope.

## Other local controls discovered

The wrapper stores or exposes local controls for:

- receipt, fulfillment, and label printer selection;
- using the receipt printer for fulfillment;
- label orientation;
- automatic printing of new fulfillment tickets;
- a terminal-registration value used to establish the Flowhub terminal cookie;
- local application and printing logs;
- a configurable application URL intended for development/support use.

Do not publish or modify terminal-registration values, cookies, receipts, logs, certificates, or customer data. These notes intentionally omit their contents.

