import uPlot from "uplot";
import { stack, type Column } from "./stack";
import { baseOptions, readTheme, timeValue, withAlpha } from "./theme";

export interface StackedSeries {
    label: string;
    color: string;
    values: Column;
}

/**
 * A stacked area chart (first series at the bottom). The legend shows each series' own value, and
 * hiding a series from the legend re-stacks the rest.
 */
export class StackedChart {
    private chart?: uPlot;
    private raw: Column[] = [];
    private timestamps: number[] = [];
    private labels = "";

    constructor(
        private readonly el: HTMLElement,
        private readonly format: (value: number | null) => string,
        private readonly axisFormat: (value: number) => string,
    ) {
        new ResizeObserver(() => this.chart?.setSize({ width: this.el.clientWidth, height: 280 })).observe(el);
    }

    setData(timestamps: number[], series: StackedSeries[]): void {
        this.timestamps = timestamps;
        this.raw = series.map(s => s.values);
        const labels = series.map(s => s.label).join("|");
        if (this.chart && labels === this.labels) {
            this.restack();
            return;
        }
        this.chart?.destroy();
        this.labels = labels;
        this.chart = this.create(series);
    }

    destroy(): void {
        this.chart?.destroy();
        this.chart = undefined;
        this.labels = "";
    }

    private create(series: StackedSeries[]): uPlot {
        const theme = readTheme();
        const { data, bands } = stack(this.raw, () => false);
        const base = baseOptions(theme, this.el.clientWidth);
        return new uPlot({
            ...base,
            axes: [base.axes![0], { ...base.axes![1], values: (_u, splits) => splits.map(v => this.axisFormat(v)) }],
            series: [
                { label: "Time", value: timeValue },
                ...series.map((s, i): uPlot.Series => ({
                    label: s.label,
                    stroke: theme.surface,
                    width: 1,
                    fill: withAlpha(s.color, 0.85),
                    value: (_u, _v, _si, index) => (index == null ? "–" : this.format(this.raw[i]?.[index] ?? null)),
                    points: { show: false },
                })),
            ],
            bands,
            hooks: { setSeries: [() => this.restack()] },
        }, [this.timestamps, ...data], this.el);
    }

    private restack(): void {
        const chart = this.chart;
        if (!chart) return;
        const { data, bands } = stack(this.raw, i => !chart.series[i + 1].show);
        chart.delBand();
        bands.forEach(b => chart.addBand(b));
        chart.setData([this.timestamps, ...data]);
    }
}
