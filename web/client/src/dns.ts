import uPlot from "uplot";
import { StackedChart } from "./stacked";
import type { Column } from "./stack";
import { baseOptions, readTheme, timeValue, token } from "./theme";

/** Matches DnsSeries in OpenRouter.Web/Dns (camelCase JSON). Counts are per minute. */
interface DnsSeries {
    from: number;
    bucketSeconds: number;
    timestamps: number[];
    outcomes: CountSeries[];
    upstream: CountSeries[];
    probes: ProbeSeries[];
}

interface CountSeries {
    name: string;
    perMinute: Column;
}

interface ProbeSeries {
    target: string;
    kind: "bind" | "other" | "forwarder";
    milliseconds: Column;
}

const RefreshMs = 60_000;

const perMinute = (v: number | null) => (v == null ? "–" : `${v < 10 ? v.toFixed(1) : Math.round(v)}/min`);
const count = (v: number) => (v >= 1000 ? `${(v / 1000).toFixed(v % 1000 === 0 ? 0 : 1)}k` : `${v}`);

class DnsView {
    private readonly seriesUrl: string;
    private readonly summaryUrl: string;
    private readonly outcomes: StackedChart;
    private readonly upstream: StackedChart;
    private readonly probesEl: HTMLElement;
    private readonly errorEl: HTMLElement | null;
    private probes?: uPlot;
    private probeKeys = "";
    private data?: DnsSeries;

    constructor(root: HTMLElement) {
        this.seriesUrl = root.dataset.seriesUrl!;
        this.summaryUrl = root.dataset.summaryUrl!;
        this.outcomes = new StackedChart(root.querySelector("[data-chart=outcomes]")!, perMinute, count);
        this.upstream = new StackedChart(root.querySelector("[data-chart=upstream]")!, perMinute, count);
        this.probesEl = root.querySelector<HTMLElement>("[data-chart=probes]")!;
        this.errorEl = root.querySelector<HTMLElement>("[data-chart-error]");
        new ResizeObserver(() => this.probes?.setSize({ width: this.probesEl.clientWidth, height: 280 })).observe(this.probesEl);
    }

    async load(): Promise<void> {
        try {
            const response = await fetch(this.seriesUrl, { headers: { Accept: "application/json" } });
            if (!response.ok) throw new Error((await response.json().catch(() => null))?.error ?? response.statusText);
            this.data = (await response.json()) as DnsSeries;
            this.render();
            this.showError(null);
        } catch (error) {
            this.showError(`Could not load chart data: ${error instanceof Error ? error.message : error}`);
        }
    }

    /** Swaps in the server-rendered tiles and probe table. */
    async loadSummary(): Promise<void> {
        const container = document.querySelector<HTMLElement>("[data-dns-summary]");
        const response = await fetch(this.summaryUrl);
        if (container && response.ok) container.innerHTML = await response.text();
    }

    rebuild(): void {
        this.outcomes.destroy();
        this.upstream.destroy();
        this.probes?.destroy();
        this.probes = undefined;
        this.probeKeys = "";
        this.render();
    }

    private render(): void {
        const data = this.data;
        if (!data) return;
        const theme = readTheme();

        // Answers: SERVFAIL on top in the failure colour, so it never sits next to the yellow.
        const outcomeColors: Record<string, string> = {
            NOERROR: theme.series[0],
            NXDOMAIN: theme.series[3],
            Other: theme.other,
            SERVFAIL: token("--series-bad"),
        };
        this.outcomes.setData(data.timestamps, data.outcomes.map(s => ({
            label: s.name, color: outcomeColors[s.name] ?? theme.other, values: s.perMinute,
        })));

        // Upstream: response-time bands on an ordinal ramp, fast to slow, then timeouts.
        this.upstream.setData(data.timestamps, data.upstream.map((s, i) => ({
            label: s.name,
            color: s.name === "Timed out" ? token("--series-bad") : token(`--rtt-${i + 1}`),
            values: s.perMinute,
        })));

        this.renderProbes(data, theme);
    }

    private renderProbes(data: DnsSeries, theme: ReturnType<typeof readTheme>): void {
        // At most eight lines (the categorical palette); the table below the tiles lists every target.
        const probes = data.probes.slice(0, 8);
        const plotData: uPlot.AlignedData = [data.timestamps, ...probes.map(p => p.milliseconds)];
        const keys = probes.map(p => p.target).join(",");
        if (this.probes && keys === this.probeKeys) {
            this.probes.setData(plotData);
            return;
        }
        this.probes?.destroy();
        this.probeKeys = keys;
        const base = baseOptions(theme, this.probesEl.clientWidth);
        this.probes = new uPlot({
            ...base,
            axes: [base.axes![0], { ...base.axes![1], values: (_u, splits) => splits.map(v => `${v} ms`) }],
            series: [
                { label: "Time", value: timeValue },
                ...probes.map((p, i): uPlot.Series => ({
                    label: p.kind === "bind" ? `bind ${p.target}` : p.kind === "other" ? `DNS ${p.target}` : p.target,
                    stroke: theme.series[i % theme.series.length],
                    width: 2,
                    value: (_u, v) => (v == null ? "–" : `${v.toFixed(1)} ms`),
                    points: { show: false },
                })),
            ],
        }, plotData, this.probesEl);
    }

    private showError(message: string | null): void {
        if (!this.errorEl) return;
        this.errorEl.hidden = message == null;
        this.errorEl.textContent = message ?? "";
    }
}

const root = document.getElementById("dns");
if (root) {
    const view = new DnsView(root);
    void view.load();

    // The server says when a new minute of DNS statistics is stored; poll only while that stream is down.
    const events = root.dataset.eventsUrl ? new EventSource(root.dataset.eventsUrl) : undefined;
    events?.addEventListener("dns", () => {
        void view.load();
        void view.loadSummary();
    });
    setInterval(() => {
        if (events?.readyState === EventSource.OPEN) return;
        void view.load();
        void view.loadSummary();
    }, RefreshMs);
    window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => view.rebuild());
}
