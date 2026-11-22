const scanner = document.querySelector("[data-barcode-scanner]");
if (scanner) {
    const toggle = scanner.querySelector("#scan-start");
    const camera = scanner.querySelector("#scan-camera");
    const video = scanner.querySelector("#scan-preview");
    const status = scanner.querySelector("#scan-status");
    let session;

    function scannedCode(result) {
        const { EAN_8, EAN_13, ITF, UPC_A, UPC_E } = ZXingBrowser.BarcodeFormat;
        const format = result.getBarcodeFormat();
        const code = result.getText();
        if ([EAN_8, EAN_13, UPC_A].includes(format) || (format === ITF && /^\d{14}$/.test(code))) return code;
        if (format !== UPC_E || !/^\d{8}$/.test(code)) return "";

        // Expand UPC-E to UPC-A for the release lookup.
        const last = code[6];
        const prefix = code.slice(0, 3);
        const check = code[7];
        if (last <= "2") return prefix + last + "0000" + code.slice(3, 6) + check;
        if (last === "3") return code.slice(0, 4) + "00000" + code.slice(4, 6) + check;
        if (last === "4") return code.slice(0, 5) + "00000" + code[5] + check;
        return code.slice(0, 6) + "0000" + last + check;
    }

    function stop(message = "") {
        const previous = session;
        session = null;
        previous?.controls?.stop();
        previous?.stream?.getTracks().forEach(track => track.stop());
        video.srcObject = null;
        camera.hidden = true;
        toggle.textContent = "Scan barcode";
        status.textContent = message;
    }

    async function start() {
        document.getElementById("lookup-field").value = "Barcode";
        if (!navigator.mediaDevices?.getUserMedia || !window.ZXingBrowser) {
            status.textContent = "Camera scanning is unavailable in this browser. Enter the barcode below.";
            return;
        }
        const current = {};
        session = current;
        toggle.textContent = "Stop scanning";
        status.textContent = "Waiting for camera access…";
        try {
            current.stream = await navigator.mediaDevices.getUserMedia({
                audio: false, video: { facingMode: { ideal: "environment" }, width: { ideal: 1280 } }
            });
            if (session !== current) {
                current.stream.getTracks().forEach(track => track.stop());
                return;
            }
            camera.hidden = false;
            status.textContent = "Hold the full barcode steady in the camera preview.";
            const reader = new ZXingBrowser.BrowserMultiFormatOneDReader();
            current.controls = await reader.decodeFromStream(current.stream, video, result => {
                if (session !== current || !result) return;
                const code = scannedCode(result);
                if (!code) return;
                stop("Barcode read: " + code);
                scanner.dispatchEvent(new CustomEvent("record-barcode", { bubbles: true, detail: code }));
            });
            if (session !== current) current.controls.stop();
        } catch (error) {
            if (session !== current) return;
            stop(error.name === "NotAllowedError"
                ? "Camera access was denied. Allow it in your browser or enter the barcode below."
                : "The camera couldn’t start. Enter the barcode below.");
        }
    }

    toggle.addEventListener("click", () => session ? stop() : start());
    window.addEventListener("pagehide", () => stop());
}
