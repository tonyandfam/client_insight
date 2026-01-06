import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import { ICompanyListItemDto } from '../models';

@Injectable({ providedIn: 'root' })
export class CompaniesService {
  private readonly _baseUrl = environment.apiBaseUrl; // e.g. "/api"

  constructor(private readonly _http: HttpClient) {}

  getCompanies(): Observable<ICompanyListItemDto[]> {
    const url = `${this._baseUrl}/companies`;
    return this._http.get<ICompanyListItemDto[]>(url);
  }
}
