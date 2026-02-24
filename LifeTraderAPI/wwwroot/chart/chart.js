/**
 * Deep Blue — Interactive Market Chart
 *
 * Uses TradingView Lightweight Charts.
 * Reads query params: symbol, tf, range.
 * Fetches candle data from same-origin backend API.
 * Supports timeframe switching, pan-left paging, and live quote polling.
 */

(function () {
    "use strict";

    // ── Config ──────────────────────────────────────────────────────────────
    const TIMEFRAMES = [
        { label: "1H",  tf: "1h",  range: "7d"   },
        { label: "1D",  tf: "1d",  range: "180d"  },
        { label: "1W",  tf: "1w",  range: "2y"    },
        { label: "1M",  tf: "1mo", range: "2y"    },
    ];

    const POLL_INTERVAL_MS = 5000;

    // ── Read query params ───────────────────────────────────────────────────
    const params   = new URLSearchParams(window.location.search);
    let symbol     = (params.get("symbol") || "AMD").toUpperCase();
    let activeTf   = params.get("tf")    || "1d";
    let activeRange = params.get("range") || "180d";

    // ── DOM refs ────────────────────────────────────────────────────────────
    const symbolLabel  = document.getElementById("symbol-label");
    const priceLabel   = document.getElementById("price-label");
    const tfBar        = document.getElementById("tf-bar");
    const chartDiv     = document.getElementById("chart-container");
    const loadingEl    = document.getElementById("loading");
    const errorBanner  = document.getElementById("error-banner");

    symbolLabel.textContent = symbol;

    // ── Chart setup ─────────────────────────────────────────────────────────
    const chart = LightweightCharts.createChart(chartDiv, {
        layout: {
            background: { type: "solid", color: "#0a0e17" },
            textColor: "#94a3b8",
            fontSize: 12,
        },
        grid: {
            vertLines:  { color: "#1e293b" },
            horzLines:  { color: "#1e293b" },
        },
        crosshair: {
            mode: LightweightCharts.CrosshairMode.Normal,
        },
        rightPriceScale: {
            borderColor: "#1e293b",
        },
        timeScale: {
            borderColor: "#1e293b",
            timeVisible: true,
            secondsVisible: false,
        },
    });

    const candleSeries = chart.addCandlestickSeries({
        upColor:          "#22c55e",
        downColor:        "#ef4444",
        borderUpColor:    "#22c55e",
        borderDownColor:  "#ef4444",
        wickUpColor:      "#22c55e",
        wickDownColor:    "#ef4444",
    });

    const volumeSeries = chart.addHistogramSeries({
        priceFormat:      { type: "volume" },
        priceScaleId:     "volume",
        color:            "#334155",
    });

    chart.priceScale("volume").applyOptions({
        scaleMargins: { top: 0.8, bottom: 0 },
        drawTicks: false,
    });

    // Responsive resize
    function resizeChart() {
        chart.applyOptions({
            width:  chartDiv.clientWidth,
            height: chartDiv.clientHeight,
        });
    }
    resizeChart();
    window.addEventListener("resize", resizeChart);

    // ── State ───────────────────────────────────────────────────────────────
    let allCandles = [];     // sorted by time ascending
    let nextTo     = null;   // paging hint for older candles
    let isFetching = false;  // guard against concurrent fetches
    let pollTimer  = null;

    // ── Timeframe buttons ───────────────────────────────────────────────────
    function renderTfButtons() {
        tfBar.innerHTML = "";
        TIMEFRAMES.forEach(function (item) {
            var btn = document.createElement("button");
            btn.className = "tf-btn" + (item.tf === activeTf ? " active" : "");
            btn.textContent = item.label;
            btn.addEventListener("click", function () {
                if (item.tf === activeTf) return;
                activeTf    = item.tf;
                activeRange = item.range;
                renderTfButtons();
                loadCandles(true);
            });
            tfBar.appendChild(btn);
        });
    }
    renderTfButtons();

    // ── Fetch candles ───────────────────────────────────────────────────────
    async function fetchCandlesFromAPI(toParam) {
        let url = "/api/market/candles?symbol=" + encodeURIComponent(symbol)
                + "&tf=" + encodeURIComponent(activeTf)
                + "&range=" + encodeURIComponent(activeRange)
                + "&limit=500";
        if (toParam) url += "&to=" + toParam;

        var resp = await fetch(url);
        if (!resp.ok) {
            var errBody = await resp.text();
            throw new Error("API " + resp.status + ": " + errBody);
        }
        return resp.json();
    }

    async function loadCandles(reset) {
        if (isFetching) return;
        isFetching = true;

        if (reset) {
            allCandles = [];
            nextTo = null;
        }

        showLoading(true);
        showError("");

        try {
            var data = await fetchCandlesFromAPI(reset ? null : nextTo);
            var newCandles = data.candles || [];

            if (newCandles.length === 0 && reset) {
                showError("No candle data available for " + symbol);
                candleSeries.setData([]);
                volumeSeries.setData([]);
                isFetching = false;
                showLoading(false);
                return;
            }

            if (reset) {
                allCandles = newCandles;
            } else {
                // Prepend older candles (avoid duplicates by time)
                var existingTimes = new Set(allCandles.map(function (c) { return c.time; }));
                var unique = newCandles.filter(function (c) { return !existingTimes.has(c.time); });
                allCandles = unique.concat(allCandles);
            }

            nextTo = data.nextTo || null;

            // Sort ascending by time (safety)
            allCandles.sort(function (a, b) { return a.time - b.time; });

            // Set chart data
            candleSeries.setData(allCandles);
            volumeSeries.setData(allCandles.map(function (c) {
                return {
                    time:  c.time,
                    value: c.volume,
                    color: c.close >= c.open ? "rgba(34,197,94,0.25)" : "rgba(239,68,68,0.25)",
                };
            }));

            if (reset) {
                chart.timeScale().fitContent();
            }

            // Update price display from last candle
            if (allCandles.length > 0) {
                var last = allCandles[allCandles.length - 1];
                updatePriceDisplay(last.close, last.open);
            }
        } catch (err) {
            console.error("[Chart] Load error:", err);
            showError("Failed to load chart data: " + err.message);
        }

        isFetching = false;
        showLoading(false);
    }

    // ── Pan-left paging ─────────────────────────────────────────────────────
    chart.timeScale().subscribeVisibleLogicalRangeChange(function (logicalRange) {
        if (!logicalRange || isFetching || !nextTo) return;

        // If user has scrolled near the left edge, fetch older candles
        if (logicalRange.from < 10) {
            loadCandles(false);
        }
    });

    // ── Live quote polling ──────────────────────────────────────────────────
    async function pollQuote() {
        try {
            var resp = await fetch("/api/market/quote?symbol=" + encodeURIComponent(symbol));
            if (!resp.ok) return;
            var data = await resp.json();
            var livePrice = data.price;

            if (allCandles.length > 0) {
                var last = allCandles[allCandles.length - 1];
                // Update the close of the last candle with live price
                last.close = livePrice;
                if (livePrice > last.high) last.high = livePrice;
                if (livePrice < last.low)  last.low  = livePrice;

                candleSeries.update(last);
                updatePriceDisplay(livePrice, last.open);
            }
        } catch (err) {
            // Silent fail for polling — transient errors are expected
        }
    }

    function startPolling() {
        stopPolling();
        pollTimer = setInterval(pollQuote, POLL_INTERVAL_MS);
    }

    function stopPolling() {
        if (pollTimer) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    function updatePriceDisplay(currentPrice, openPrice) {
        var formatted = "$" + currentPrice.toFixed(2);
        priceLabel.textContent = formatted;
        priceLabel.className = "price-label" + (currentPrice >= openPrice ? " up" : " down");
    }

    function showLoading(visible) {
        loadingEl.className = "loading-overlay" + (visible ? "" : " hidden");
    }

    function showError(msg) {
        if (msg) {
            errorBanner.textContent = msg;
            errorBanner.className = "error-banner visible";
        } else {
            errorBanner.textContent = "";
            errorBanner.className = "error-banner";
        }
    }

    // ── Crosshair tooltip (OHLC legend) ─────────────────────────────────────
    var legendEl = document.createElement("div");
    legendEl.style.cssText = "position:absolute;top:12px;left:16px;z-index:5;font-size:12px;color:#94a3b8;pointer-events:none;font-family:monospace;line-height:1.6;";
    chartDiv.appendChild(legendEl);

    chart.subscribeCrosshairMove(function (param) {
        if (!param || !param.time) {
            legendEl.innerHTML = "";
            return;
        }

        var candle = param.seriesData.get(candleSeries);
        if (!candle) {
            legendEl.innerHTML = "";
            return;
        }

        var changeColor = candle.close >= candle.open ? "#22c55e" : "#ef4444";
        legendEl.innerHTML =
            "<span style='color:#e0e6ed;font-weight:600;'>" + symbol + "</span>  " +
            "O <span style='color:" + changeColor + "'>" + candle.open.toFixed(2) + "</span>  " +
            "H <span style='color:" + changeColor + "'>" + candle.high.toFixed(2) + "</span>  " +
            "L <span style='color:" + changeColor + "'>" + candle.low.toFixed(2)  + "</span>  " +
            "C <span style='color:" + changeColor + "'>" + candle.close.toFixed(2) + "</span>";
    });

    // ── Boot ────────────────────────────────────────────────────────────────
    loadCandles(true).then(function () {
        startPolling();
    });

})();
