# Local UI assets

- The active UI uses custom css/app.css and Coming Soon, with no Bootstrap dependency. All fonts/styles are served locally; no CDN request occurs at runtime. Text uses repository CRLF endings.

- SortableJS 1.15.6: https://github.com/SortableJS/Sortable/tree/1.15.6; complete ES module from https://cdn.jsdelivr.net/npm/sortablejs@1.15.6/modular/sortable.complete.esm.js (MIT; LICENSE alongside). Vendored unchanged except CRLF line endings. Imported locally by ordering.js; no runtime CDN request.

- Coming Soon regular by Open Window: https://github.com/google/fonts/blob/0fc4c4f4a270a62fa1b769c7289a994cbf199b03/apache/comingsoon/ComingSoon-Regular.ttf (Apache 2.0; coming-soon-LICENSE.txt alongside). Vendored TTF unchanged; SHA-256 `cf81388f587ff6122de1a705d1070b8f00abccdba66108509908b5d10715068c`.
