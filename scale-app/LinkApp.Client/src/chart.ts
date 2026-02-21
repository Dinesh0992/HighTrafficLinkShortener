import { Chart, registerables } from 'chart.js';
import  type { DailyClickCount } from './types';

Chart.register(...registerables);

let currentChart: Chart | null = null;

export function renderAnalyticsChart(canvasId: string, history: DailyClickCount[]) {
    const ctx = document.getElementById(canvasId) as HTMLCanvasElement;
    
    // If a chart already exists, destroy it before creating a new one (important for re-searching)
    if (currentChart) {
        currentChart.destroy();
    }

    const labels = history.map(h => new Date(h.date).toLocaleDateString(undefined, { month: 'short', day: 'numeric' }));
    const data = history.map(h => h.count);

    currentChart = new Chart(ctx, {
        type: 'line',
        data: {
            labels: labels,
            datasets: [{
                label: 'Clicks per Day',
                data: data,
                borderColor: '#60a5fa', // Blue-400
                backgroundColor: 'rgba(96, 165, 250, 0.1)',
                borderWidth: 3,
                fill: true,
                tension: 0.4,
                pointBackgroundColor: '#3b82f6',
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
                legend: { display: false }
            },
            scales: {
                y: {
                    beginAtZero: true,
                    grid: { color: 'rgba(255, 255, 255, 0.1)' },
                    ticks: { color: '#94a3b8' }
                },
                x: {
                    grid: { display: false },
                    ticks: { color: '#94a3b8' }
                }
            }
        }
    });
}