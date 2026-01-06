import { CommonModule, DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BehaviorSubject, combineLatest, map, switchMap, catchError, of, startWith } from 'rxjs';
import {
  ICompanyClientFeedArticleDto,
  ICompanyClientFeedDto,
} from '../../client-insight-api/models';
import { CompanyFeedService } from '../../client-insight-api/services';

type VmGroup = ICompanyClientFeedDto & { filteredCount: number };

@Component({
  selector: 'app-company-feed',
  standalone: true,
  imports: [CommonModule, FormsModule, DatePipe],
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

  // refresh trigger
  private readonly _refresh$ = new BehaviorSubject<void>(undefined);

  // view state
  readonly vm$ = combineLatest({
    refresh: this._refresh$,
  }).pipe(
    switchMap(() => {
      if (!this.companyId) {
        return of({
          loading: false,
          error: '',
          missingCompanyId: true,
          groups: [] as VmGroup[],
          totalClients: 0,
          totalArticles: 0,
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
          map((groups) => {
            const q = (this.search ?? '').trim().toLowerCase();

            const filteredGroups: VmGroup[] = (groups ?? [])
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
          }),
          startWith({
            loading: true,
            error: '',
            missingCompanyId: false,
            groups: [] as VmGroup[],
            totalClients: 0,
            totalArticles: 0,
          }),
          catchError((err) => {
            const msg = err?.error?.message || err?.message || 'Failed to load feed.';
            return of({
              loading: false,
              error: msg,
              missingCompanyId: false,
              groups: [] as VmGroup[],
              totalClients: 0,
              totalArticles: 0,
            });
          })
        );
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
}
