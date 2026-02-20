export interface DailyClickCount {
    date: string;
    count: number;
}

export interface LinkStats {
    shortCode: string;
    totalClicks: number;
    uniqueVisitors: number;
    lastAccessed: string | null;
    clickHistory: DailyClickCount[];
}

export interface TrendingLink {
    shortCode: string;
    clicks: number;
}