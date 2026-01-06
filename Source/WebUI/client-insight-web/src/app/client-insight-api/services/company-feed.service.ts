import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { ICompanyFeedQuery } from '../models';
import { ICompanyClientFeedDto } from '../models/company-feed.models';

@Injectable({ providedIn: 'root' })
export class CompanyFeedService {
  private readonly _baseUrl = environment.apiBaseUrl;

  constructor(private readonly _http: HttpClient) {}

  getCompanyFeed(
    companyId: string,
    query: ICompanyFeedQuery = {}
  ): Observable<ICompanyClientFeedDto[]> {
    if (!companyId) throw new Error('companyId is required');

    let params = new HttpParams();

    if (query.days != null) params = params.set('days', String(query.days));
    if (query.limit != null) params = params.set('limit', String(query.limit));
    if (query.activeOnly != null) params = params.set('activeOnly', String(query.activeOnly));
    if (query.minScore != null) params = params.set('minScore', String(query.minScore));

    const url = `${this._baseUrl}/companies/${companyId}/feed`;
    return this._http.get<ICompanyClientFeedDto[]>(url, { params });
  }
}
