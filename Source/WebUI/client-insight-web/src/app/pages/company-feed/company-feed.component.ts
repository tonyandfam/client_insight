import { CommonModule, DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  BehaviorSubject,
  combineLatest,
  map,
  switchMap,
  catchError,
  of,
  startWith,
  debounceTime,
  distinctUntilChanged,
  shareReplay,
} from 'rxjs';
import {
  ICompanyClientFeedArticleDto,
  ICompanyClientFeedDto,
} from '../../client-insight-api/models';
import { CompanyFeedService } from '../../client-insight-api/services';
import { CompanySelectComponent } from '../../components/company-select/company-select.component';

type VmGroup = ICompanyClientFeedDto & { filteredCount: number };

type FeedState =
  | {
      loading: true;
      error: '';
      missingCompanyId: boolean;
      groups: ICompanyClientFeedDto[];
    }
  | {
      loading: false;
      error: string;
      missingCompanyId: boolean;
      groups: ICompanyClientFeedDto[];
    };

@Component({
  selector: 'app-company-feed',
  standalone: true,
  imports: [CommonModule, FormsModule, DatePipe, CompanySelectComponent],
  templateUrl: './company-feed.component.html',
  styleUrls: ['./company-feed.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CompanyFeedComponent {
  // TODO: replace this with your real "company context" later
  companyId = ''; // paste a guid while developing, e.g. 'b8c0...'

  // query inputs
  days = 30;
  limit = 50; // per-client
  activeOnly = true;
  minScore: number | null = null;

  // UI filters (client-side)
  search = '';

  // expansion state
  expanded = new Set<string>();

  // refresh trigger (server reload)
  private readonly _refresh$ = new BehaviorSubject<void>(undefined);

  // search trigger (client-side filter only)
  private readonly _search$ = new BehaviorSubject<string>('');

  readonly placeholderImg = 'assets/news-placeholder.png';

  getArticleImageUrl(url?: string | null): string {
    const u = (url ?? '').trim();
    return u ? u : this.placeholderImg;
  }

  onArticleImgError(ev: Event): void {
    const img = ev.target as HTMLImageElement | null;
    if (!img) return;

    // Prevent infinite loop if placeholder is missing
    if (img.src.includes('assets/news-placeholder.png')) return;

    img.src = this.placeholderImg;
  }

  /** called from template so search updates as you type */
  setSearch(v: string): void {
    this.search = v ?? '';
    this._search$.next(this.search);
  }

  private readonly feedState$ = this._refresh$.pipe(
    switchMap(() => {
      if (!this.companyId) {
        return of<FeedState>({
          loading: false,
          error: '',
          missingCompanyId: true,
          groups: [],
        });
      }

      return this._feed
        .getCompanyFeed(this.companyId, {
          days: this.days,
          limit: this.limit,
          activeOnly: this.activeOnly,
          minScore: this.minScore,
        })
        .pipe(
          map(
            (groups): FeedState => ({
              loading: false,
              error: '',
              missingCompanyId: false,
              groups: groups ?? [],
            })
          ),
          startWith<FeedState>({
            loading: true,
            error: '',
            missingCompanyId: false,
            groups: [],
          }),
          catchError((err) => {
            const msg = err?.error?.message || err?.message || 'Failed to load feed.';
            return of<FeedState>({
              loading: false,
              error: msg,
              missingCompanyId: false,
              groups: [],
            });
          })
        );
    }),
    // cache last loaded feed so search changes don’t cause re-fetches
    shareReplay({ bufferSize: 1, refCount: true })
  );

  private readonly searchQuery$ = this._search$.pipe(
    debounceTime(150),
    map((s) => (s ?? '').trim().toLowerCase()),
    distinctUntilChanged()
  );

  // view state (reacts to search changes without reload)
  readonly vm$ = combineLatest({
    state: this.feedState$,
    q: this.searchQuery$,
  }).pipe(
    map(({ state, q }) => {
      // passthrough for loading / error / missing company
      if (state.loading || state.error || state.missingCompanyId) {
        return {
          loading: state.loading,
          error: state.error,
          missingCompanyId: state.missingCompanyId,
          groups: [] as VmGroup[],
          totalClients: 0,
          totalArticles: 0,
        };
      }

      const filteredGroups: VmGroup[] = (state.groups ?? [])
        .map((g) => {
          const articles = (g.articles ?? []).filter((a) => this.matches(q, g, a));
          return { ...g, articles, filteredCount: articles.length };
        })
        .filter((g) => g.filteredCount > 0 || !q); // if searching, hide empty groups

      // Auto-expand first group on first load (optional nice UX)
      if (filteredGroups.length && this.expanded.size === 0) {
        this.expanded.add(filteredGroups[0].clientId);
      }

      return {
        loading: false,
        error: '',
        missingCompanyId: false,
        groups: filteredGroups,
        totalClients: filteredGroups.length,
        totalArticles: filteredGroups.reduce((sum, g) => sum + (g.articles?.length ?? 0), 0),
      };
    })
  );

  constructor(private readonly _feed: CompanyFeedService) {}

  refresh(): void {
    this._refresh$.next();
  }

  toggleClient(clientId: string): void {
    if (this.expanded.has(clientId)) this.expanded.delete(clientId);
    else this.expanded.add(clientId);
  }

  isExpanded(clientId: string): boolean {
    return this.expanded.has(clientId);
  }

  expandAll(groups: VmGroup[]): void {
    for (const g of groups) this.expanded.add(g.clientId);
  }

  collapseAll(): void {
    this.expanded.clear();
  }

  trackGroup = (_: number, g: VmGroup) => g.clientId;
  trackArticle = (_: number, a: ICompanyClientFeedArticleDto) => a.articleId;

  formatScore(score?: number | null): string {
    if (score == null) return '-';
    return Number(score).toFixed(2);
  }

  scoreClass(score?: number | null): string {
    const s = score ?? 0;
    if (s >= 0.85) return 'badge badge--vh';
    if (s >= 0.7) return 'badge badge--h';
    if (s >= 0.5) return 'badge badge--m';
    return 'badge badge--l';
  }

  safeDomain(url: string): string {
    try {
      return new URL(url).hostname.replace(/^www\./, '');
    } catch {
      return '';
    }
  }

  private matches(q: string, g: ICompanyClientFeedDto, a: ICompanyClientFeedArticleDto): boolean {
    if (!q) return true;

    const hay = [
      g.clientName,
      g.clientWebsite,
      g.clientAddress,
      a.title,
      a.englishSummary,
      a.reasonForScore,
      a.conversationAngle,
      a.sourceCountry,
      a.articleLanguage,
      a.url,
    ]
      .filter(Boolean)
      .join(' ')
      .toLowerCase();

    return hay.includes(q);
  }

  readonly relevanceTooltip = `0–2: weak mention, no conversation value
3–4: client is mentioned but generic/low value
5–6: meaningful update (project, funding, contract, award, expansion)
7–8: strong conversation starter (impact, timeline, leadership change, controversy, milestone)
9–10: urgent/high impact (major win/loss, crisis, legal action, acquisition, shutdown, major project launch)`;
}
