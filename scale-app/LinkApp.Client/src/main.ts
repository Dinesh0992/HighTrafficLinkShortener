import './style.css'
import type { LinkStats, TrendingLink } from './types';
import { renderAnalyticsChart } from './chart';

const API_BASE = 'http://localhost:5082/api';

// --- SEED LOGIC ---
document.querySelector<HTMLButtonElement>('#seedBtn')!.addEventListener('click', async () => {
  const status = document.getElementById('status')!;
  const btn = document.getElementById('seedBtn') as HTMLButtonElement;
  
  status.innerText = "Seeding... please wait.";
  btn.disabled = true;

  try {
    // Explicitly using the /api/seed path
    const res = await fetch(`${API_BASE}/seed`, { 
        method: 'POST',
        headers: { 'Accept': 'text/plain' } 
    });

    if (!res.ok) throw new Error(`Status ${res.status}`);

    const msg = await res.text();
    status.innerText = "Success: " + msg;
    await loadTrending(); // Refresh the list
  } catch (e) {
    status.innerText = "Error: " + e;
  } finally {
    btn.disabled = false;
  }
});

// --- TRENDING LOGIC ---
async function loadTrending(): Promise<void> {
    try {
        const res = await fetch(`${API_BASE}/stats/trending`);
        if (!res.ok) throw new Error('Failed to load trending');
        
        let data = await res.json();
        if (!Array.isArray(data)) data = data.data || [];
        
        const list = document.getElementById('trending-list')!;
        
        if (data.length === 0) {
            list.innerHTML = '<p class="text-gray-500 text-sm">No trending links yet</p>';
            return;
        }
        
        list.innerHTML = data.map((link: TrendingLink) => `
            <div class="flex justify-between items-center p-3 bg-gray-800 rounded-lg border border-gray-700 hover:border-blue-500 transition cursor-pointer">
                <span class="font-mono text-blue-400">/${link.shortCode}</span>
                <span class="text-xs font-bold bg-blue-900 text-blue-100 px-2 py-1 rounded-full">${link.clicks} clicks</span>
            </div>
        `).join('');
    } catch (err) {
        console.error('Error:', err);
    }
}

// --- SEARCH LOGIC ---
/*
async function getStats(code: string): Promise<void> {
    const statsContainer = document.getElementById('stats-display')!;
    try {
        const res = await fetch(`${API_BASE}/stats/${code}`);
        if (!res.ok) throw new Error('Not found');
        
        const data: LinkStats = await res.json();
        
        document.getElementById('total-clicks')!.innerText = data.totalClicks.toString();
        document.getElementById('unique-visitors')!.innerText = data.uniqueVisitors.toString();
        document.getElementById('last-accessed')!.innerText = data.lastAccessed 
            ? new Date(data.lastAccessed).toLocaleString() 
            : 'Never';
            
        statsContainer.classList.remove('hidden');
    } catch (err) {
        alert('Code not found');
    }
}
*/
document.getElementById('search-btn')?.addEventListener('click', () => {
    const code = (document.getElementById('search-input') as HTMLInputElement).value;
    if (code) getStats(code);
});

loadTrending();

async function getStats(code: string): Promise<void> {
    const statsContainer = document.getElementById('stats-display')!;
    try {
        const res = await fetch(`${API_BASE}/stats/${code}`);
        if (!res.ok) throw new Error('Not found');
        
        const data: LinkStats = await res.json();
        
        // 1. Update text stats
        document.getElementById('total-clicks')!.innerText = data.totalClicks.toString();
        document.getElementById('unique-visitors')!.innerText = data.uniqueVisitors.toString();
        document.getElementById('last-accessed')!.innerText = data.lastAccessed 
            ? new Date(data.lastAccessed).toLocaleString() 
            : 'Never';
            
        // 2. Render the Chart
        if (data.clickHistory && data.clickHistory.length > 0) {
            renderAnalyticsChart('analyticsChart', data.clickHistory);
        }

        statsContainer.classList.remove('hidden');
    } catch (err) {
        alert('Code not found or server error');
        console.error(err);
    }
}