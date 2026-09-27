window.roomly = window.roomly || {};
window.roomly.getTimeZone = () => Intl.DateTimeFormat().resolvedOptions().timeZone;

window.roomlyCharts = {
    render: (elementId, options, totalSlots = 0) => {
        const element = document.getElementById(elementId);
        if (!element) return;
        if (typeof ApexCharts === "undefined") {
            element.innerHTML = '<div role="status" style="padding:3rem 1rem;text-align:center;color:#748098;font:12px sans-serif">Charts are temporarily unavailable. Reload the page to try again.</div>';
            return;
        }

        const percentage = (value) => `${Number(value).toFixed(1)}%`;
        if (options.dataLabels?.formatter === "__percent__") options.dataLabels.formatter = percentage;
        if (options.xaxis?.labels?.formatter === "__percent__") options.xaxis.labels.formatter = percentage;
        if (options.tooltip?.y?.formatter === "__percent__") options.tooltip.y.formatter = percentage;
        if (options.plotOptions?.pie?.donut?.labels?.total?.formatter === "__totalSlots__") {
            options.plotOptions.pie.donut.labels.total.formatter = () => String(totalSlots);
        }

        element._apexChart?.destroy();
        element._apexChart = new ApexCharts(element, options);
        element._apexChart.render();
    }
};
