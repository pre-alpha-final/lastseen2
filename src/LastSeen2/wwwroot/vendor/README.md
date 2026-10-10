# Local UI assets

- Bootswatch Sketchy 5.3.8: https://cdn.jsdelivr.net/npm/bootswatch@5.3.8/dist/sketchy/bootstrap.min.css (MIT; license alongside). Removed Google Fonts import and source-map reference; dark overrides are in css/app.css.
- Neucha Latin regular and Cabin Sketch Latin bold: @fontsource/neucha and @fontsource/cabin-sketch 5.2.6, downloaded from jsDelivr. SIL Open Font Licenses included in fonts/.
- All fonts/styles are served locally; no CDN request occurs at runtime. Text uses repository CRLF endings.

- SortableJS 1.15.6: https://github.com/SortableJS/Sortable/tree/1.15.6; complete ES module from https://cdn.jsdelivr.net/npm/sortablejs@1.15.6/modular/sortable.complete.esm.js (MIT; LICENSE alongside). Vendored unchanged except CRLF line endings. Imported locally by ordering.js; no runtime CDN request.
