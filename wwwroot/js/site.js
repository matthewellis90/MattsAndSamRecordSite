for (const cover of document.querySelectorAll(".record-cover")) {
    const showFallback = () => cover.hidden = true;
    cover.addEventListener("error", showFallback, { once: true });

    if (cover.complete && cover.naturalWidth === 0) {
        showFallback();
    }
}

const search = document.querySelector("[data-suggest]");
if (search) {
    const list = document.getElementById(search.getAttribute("aria-controls"));
    let timer;
    let pending;

    const reset = () => {
        clearTimeout(timer);
        pending?.abort();
        list.hidden = true;
        list.replaceChildren();
    };

    search.addEventListener("input", () => {
        reset();
        const term = search.value.trim();
        if (term.length < 2) return;

        timer = setTimeout(async () => {
            pending = new AbortController();
            try {
                const response = await fetch(search.dataset.suggest + "?" + new URLSearchParams({ search: term }), {
                    signal: pending.signal
                });
                if (!response.ok) return;
                const records = await response.json();
                if (term !== search.value.trim() || document.activeElement !== search) return;

                const items = records.map(record => {
                    const row = document.createElement("li");
                    const link = document.createElement("a");
                    link.href = record.url;
                    link.textContent = [record.name, record.artist, record.year].filter(Boolean).join(" · ");
                    row.append(link);
                    return row;
                });
                list.replaceChildren(...items);
                list.hidden = items.length === 0;
            } catch {
                // The standalone search form remains available.
            }
        }, 230);
    });

    document.addEventListener("pointerdown", event => {
        if (!search.form.contains(event.target)) reset();
    });
    search.form.addEventListener("keydown", event => {
        if (event.key === "Escape") reset();
        if (event.key === "ArrowDown" && event.target === search && !list.hidden) {
            event.preventDefault();
            list.querySelector("a")?.focus();
        }
    });
}

if (document.querySelector("[data-account-preparing]"))
    window.setTimeout(() => window.location.reload(), 8000);

const recoveryToken = document.querySelector("[data-recovery-token]");
if (recoveryToken && /^#[A-Za-z0-9_-]{43}$/.test(window.location.hash)) {
    recoveryToken.value = window.location.hash.slice(1);
    window.history.replaceState(null, "", window.location.pathname + window.location.search);
}

const recentCollection = document.querySelector("[data-recent-collection]");
if (recentCollection) {
    const preference = "record-room.recent-open";
    let saved = null;
    try { saved = localStorage.getItem(preference); } catch { /* Use the screen-size default. */ }
    recentCollection.open = saved === null ? !matchMedia("(max-width: 43rem)").matches : saved === "true";
    recentCollection.querySelector("summary").addEventListener("click", () => {
        try { localStorage.setItem(preference, String(!recentCollection.open)); } catch { /* Native disclosure still works. */ }
    });
}

const catalogFilters = document.querySelector("[data-catalog-filters]");
if (catalogFilters) catalogFilters.open = !matchMedia("(max-width: 43rem)").matches;

const sleeveGallery = document.querySelector("[data-sleeve-gallery]");
if (sleeveGallery) {
    const controls = sleeveGallery.querySelector("[data-sleeve-controls]");
    const panels = Array.from(sleeveGallery.querySelectorAll("[data-sleeve-side]"));
    const buttons = Array.from(controls.querySelectorAll("button"));
    const showSide = selected => {
        for (const button of buttons) {
            const active = button === selected;
            button.setAttribute("aria-pressed", String(active));
            panels.find(panel => panel.id === button.getAttribute("aria-controls")).hidden = !active;
        }
    };
    for (const button of buttons) button.addEventListener("click", () => showSide(button));
    showSide(buttons[0]);
    controls.hidden = false;
}

for (const controls of document.querySelectorAll("[data-share-controls]")) {
    const copy = controls.querySelector("[data-copy-share]");
    const share = controls.querySelector("[data-native-share]");
    const field = controls.querySelector("#collection-share-url");
    const status = controls.querySelector("[data-share-status]");
    const url = controls.dataset.shareUrl;
    if (!url) continue;

    if (navigator.clipboard || field) {
        copy.hidden = false;
        copy.addEventListener("click", async () => {
            try {
                await navigator.clipboard.writeText(url);
                status.textContent = "Link copied.";
            } catch {
                if (field) {
                    field.focus();
                    field.select();
                }
                status.textContent = field ? "Copy the selected link." : "Copy the link from your browser’s address bar.";
            }
        });
    }

    const shareData = { title: controls.dataset.shareTitle, url };
    if (navigator.share && (!navigator.canShare || navigator.canShare(shareData))) {
        share.hidden = false;
        share.addEventListener("click", async () => {
            try {
                await navigator.share(shareData);
            } catch (error) {
                if (error.name !== "AbortError")
                    status.textContent = copy.hidden ? "Copy the link from your browser’s address bar." : "Sharing is unavailable. Use Copy link.";
            }
        });
    }
}

const spotifyPlayer = document.querySelector("[data-spotify-player]");
if (spotifyPlayer) {
    spotifyPlayer.hidden = false;
    spotifyPlayer.addEventListener("click", () => {
        const frame = document.createElement("iframe");
        frame.src = spotifyPlayer.dataset.spotifyPlayer;
        frame.title = spotifyPlayer.dataset.spotifyTitle;
        frame.className = "spotify-frame";
        frame.height = frame.src.includes("/embed/track/") ? "152" : "352";
        frame.allow = "autoplay; encrypted-media; fullscreen; picture-in-picture";
        frame.referrerPolicy = "no-referrer";
        spotifyPlayer.closest(".spotify-listen").append(frame);
        spotifyPlayer.hidden = true;
        frame.focus();
    }, { once: true });
}

const lookup = document.querySelector("[data-lookup]");
if (lookup) {
    const form = document.querySelector(".edit-form");
    const button = document.getElementById("lookup-submit");
    const field = document.getElementById("lookup-field");
    const term = document.getElementById("lookup-term");
    const status = document.getElementById("lookup-status");
    const source = document.getElementById("lookup-source");
    const duplicates = document.getElementById("lookup-duplicates");
    const results = document.getElementById("lookup-results");
    const cover = document.getElementById("lookup-cover");
    const coverUrl = form.elements.namedItem("CoverUrl");
    const artists = form.elements.namedItem("ArtistId");
    const newArtist = form.elements.namedItem("NewArtistName");
    const currentId = form.elements.namedItem("Id").value;
    const filled = new Map();
    let lookupRequest;

    function previewCover() {
        cover.hidden = true;
        cover.removeAttribute("src");
        if (coverUrl.value.startsWith("https://")) cover.src = coverUrl.value;
    }
    cover.addEventListener("load", () => { cover.hidden = false; });
    cover.addEventListener("error", () => { cover.hidden = true; });
    coverUrl.addEventListener("change", previewCover);
    previewCover();
    artists.addEventListener("change", () => {
        if (newArtist && artists.value) newArtist.value = "";
    });
    newArtist?.addEventListener("input", () => {
        if (newArtist.value.trim()) artists.value = "";
    });

    function applyPressing(candidate) {
        const fields = {
            Name: candidate.title, Format: candidate.format, Label: candidate.label,
            Country: candidate.country, Year: candidate.releaseYear, PressingYear: candidate.year,
            CatalogNumber: candidate.catalogueNumber, Genre: candidate.genre, Barcode: candidate.barcode,
            CoverUrl: candidate.coverUrl,
            SpotifyUrl: candidate.spotifyUrl
        };
        for (const [name, value] of Object.entries(fields)) {
            const control = form.elements.namedItem(name);
            if (name === "SpotifyUrl" && control.value && control.value !== filled.get(name)) continue;
            if (value) {
                control.value = String(value).slice(0, control.maxLength > 0 ? control.maxLength : undefined);
                filled.set(name, control.value);
            } else {
                if (control.value === filled.get(name)) control.value = "";
                filled.delete(name);
            }
        }
        form.elements.namedItem("MusicBrainzId").value = candidate.id || "";
        form.elements.namedItem("DiscogsId").value = candidate.discogsId || candidate.linkedDiscogsId || "";
        if (candidate.medium != null) form.elements.namedItem("Medium").value = String(candidate.medium);
        const kind = ["Album", "Single", "EP", "Compilation"].indexOf(candidate.kind);
        if (kind >= 0) form.elements.namedItem("Kind").value = String(kind);
        const normalize = text => text.trim().toLowerCase();
        const credit = normalize(candidate.artist);
        const options = [...artists.options].filter(option => option.value);
        const aliases = options.filter(option => normalize(option.text).replace(/^the /, "") === credit.replace(/^the /, ""));
        const artist = options.find(option => normalize(option.text) === credit) ?? (aliases.length === 1 ? aliases[0] : null);
        if (artist) {
            artists.value = artist.value;
            if (newArtist) newArtist.value = "";
        } else if (newArtist && candidate.artist) {
            artists.value = "";
            newArtist.value = candidate.kind === "Compilation" && candidate.artist === "Various Artists"
                ? "" : candidate.artist.slice(0, newArtist.maxLength);
        }
        previewCover();
        status.textContent = newArtist || artist || !candidate.artist
            ? "Pressing selected. Review the details before saving."
            : "Pressing selected. Check the artist before saving.";
        if (!candidate.coverUrl) status.textContent += " No cover art found for this release.";
        source.replaceChildren();
        if (candidate.discogsId) source.append(discogsLink(candidate.discogsId));
        source.hidden = !source.childNodes.length;
        showDuplicates(candidate.collectionMatches);
        form.elements.namedItem("Name").focus();
    }

    function showDuplicates(matches = []) {
        matches = matches.filter(match => String(match.id) !== currentId);
        duplicates.replaceChildren();
        duplicates.hidden = matches.length === 0;
        if (duplicates.hidden) return;

        const heading = document.createElement("strong");
        heading.textContent = "Already in your collection";
        const list = document.createElement("ul");
        for (const match of new Map(matches.map(record => [record.id, record])).values()) {
            const item = document.createElement("li");
            const link = document.createElement("a");
            link.href = lookup.dataset.existingRecord.replace(/0$/, match.id);
            link.textContent = [match.title, match.artist, match.catalogueNumber, match.pressingYear].filter(Boolean).join(" · ");
            item.append(link, " (" + match.reason + ")");
            list.append(item);
        }
        duplicates.append(heading, list);
    }

    function discogsLink(id) {
        const link = document.createElement("a");
        link.href = "https://www.discogs.com/release/" + id;
        link.target = "_blank";
        link.rel = "noopener";
        link.textContent = "Data provided by Discogs.";
        return link;
    }

    async function selectPressing(candidate) {
        if (candidate.isDetailed) {
            applyPressing(candidate);
            return;
        }
        lookupRequest?.abort();
        const request = new AbortController();
        lookupRequest = request;
        status.textContent = "Loading release details and artwork…";
        try {
            const parameters = candidate.discogsId ? { discogsId: candidate.discogsId, barcode: candidate.barcode } : { id: candidate.id };
            const response = await fetch(lookup.dataset.lookupDetails + "?" + new URLSearchParams(parameters),
                { signal: request.signal });
            if (response.ok) candidate = await response.json();
            else if (response.status === 404 && candidate.discogsId) {
                status.textContent = "This pressing could not be verified. Try another match or enter the details below.";
                return;
            }
        } catch {
            // The search result can still be reviewed if the detail lookup fails.
        }
        if (!request.signal.aborted) applyPressing(candidate);
    }

    async function findPressings() {
        if (!term.value.trim()) {
            term.focus();
            status.textContent = "Enter a release to look up.";
            return;
        }
        lookupRequest?.abort();
        const request = new AbortController();
        lookupRequest = request;
        results.replaceChildren();
        source.hidden = true;
        showDuplicates();
        button.disabled = true;
        if (field.value === "Barcode" && newArtist) form.elements.namedItem("Barcode").value = term.value.trim();
        status.textContent = "Searching for pressings…";
        try {
            const query = new URLSearchParams({
                field: field.value,
                term: term.value.trim(),
                artist: document.getElementById("lookup-artist").value.trim()
            });
            const response = await fetch(lookup.dataset.lookup + "?" + query, { signal: request.signal });
            const lookupResult = await response.json();
            if (request.signal.aborted) return;
            if (!response.ok) {
                showDuplicates(lookupResult.rawMatches);
                status.textContent = lookupResult.message || "Lookup is unavailable. Enter the pressing details below.";
                return;
            }
            const records = lookupResult.candidates;
            showDuplicates([...(lookupResult.rawMatches || []), ...records.flatMap(candidate => candidate.collectionMatches || [])]);
            status.textContent = records.length ? "Choose the pressing that matches your copy." : "No matches. Try another search or enter the details below.";
            for (const candidate of records) {
                const row = document.createElement("li");
                const choice = document.createElement("button");
                const title = document.createElement("strong");
                const details = document.createElement("span");
                title.textContent = [candidate.title, candidate.artist].filter(Boolean).join(" · ");
                details.textContent = [candidate.year, candidate.country, candidate.format, candidate.label,
                    candidate.catalogueNumber, candidate.barcode, candidate.edition].filter(Boolean).join(" · ");
                choice.type = "button";
                choice.className = "button";
                choice.append(title, details);
                choice.addEventListener("click", () => selectPressing(candidate));
                row.append(choice);
                if (candidate.discogsId) row.append(discogsLink(candidate.discogsId));
                results.append(row);
            }
        } catch {
            if (!request.signal.aborted) status.textContent = "Lookup is unavailable. Enter the pressing details below.";
        } finally {
            if (lookupRequest === request) button.disabled = false;
        }
    }

    button.addEventListener("click", findPressings);
    lookup.addEventListener("record-barcode", event => {
        field.value = "Barcode";
        document.getElementById("lookup-artist").value = "";
        term.value = event.detail;
        const barcode = form.elements.namedItem("Barcode");
        if (!barcode.value.trim()) barcode.value = event.detail;
        findPressings();
    });
    lookup.addEventListener("keydown", event => {
        if (event.key === "Enter" && event.target.matches("input")) {
            event.preventDefault();
            findPressings();
        }
    });
    if (lookup.dataset.initialLookup === "Barcode")
        document.getElementById("scan-start")?.focus({ preventScroll: true });
    if (lookup.dataset.initialLookup === "Catalogue")
        term.focus({ preventScroll: true });
}
