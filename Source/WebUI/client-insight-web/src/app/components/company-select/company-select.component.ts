import { CommonModule, DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  forwardRef,
  Input,
  OnDestroy,
  OnInit,
} from '@angular/core';
import { ControlValueAccessor, FormsModule, NG_VALUE_ACCESSOR } from '@angular/forms';
import { Subscription } from 'rxjs';

import { CompaniesService } from '../../client-insight-api/services/companies.service';
import { ICompanyListItemDto } from '../../client-insight-api/models/companies.models';

@Component({
  selector: 'app-company-select',
  standalone: true,
  imports: [CommonModule, FormsModule, DatePipe],
  templateUrl: './company-select.component.html',
  styleUrl: './company-select.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => CompanySelectComponent),
      multi: true,
    },
  ],
})
export class CompanySelectComponent implements OnInit, OnDestroy, ControlValueAccessor {
  @Input() label = 'Company';
  @Input() placeholder = 'Select a company…';
  @Input() disabled = false;

  loading = false;
  error = '';

  companies: ICompanyListItemDto[] = [];

  // CVA value: selected companyId (Guid string)
  value: string | null = null;

  private _sub = new Subscription();
  private _onChange: (value: string | null) => void = () => {};
  private _onTouched: () => void = () => {};

  constructor(
    private readonly _companies: CompaniesService,
    private readonly _cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.loading = true;
    this.error = '';

    const s = this._companies.getCompanies().subscribe({
      next: (items) => {
        this.companies = items ?? [];
        this.loading = false;

        // If a value already exists but isn't in list anymore, clear it.
        if (this.value && !this.companies.some((c) => c.companyId === this.value)) {
          this.setValue(null);
        }

        this._cdr.markForCheck();
      },
      error: (err) => {
        this.loading = false;
        this.error = err?.error?.message || err?.message || 'Failed to load companies.';
        this._cdr.markForCheck();
      },
    });

    this._sub.add(s);
  }

  ngOnDestroy(): void {
    this._sub.unsubscribe();
  }

  // ControlValueAccessor
  writeValue(val: string | null): void {
    this.value = val ?? null;
  }

  registerOnChange(fn: (value: string | null) => void): void {
    this._onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this._onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.disabled = isDisabled;
  }

  onSelectChange(raw: string): void {
    this._onTouched();

    const next = raw ? raw : null;
    this.setValue(next);
  }

  private setValue(next: string | null): void {
    this.value = next;
    this._onChange(next);
  }

  displayName(c: ICompanyListItemDto): string {
    return c.nickname?.trim() ? c.nickname.trim() : c.companyId;
  }

  meta(c: ICompanyListItemDto): string {
    const active = c.activeClientCount ?? 0;
    const total = c.totalClientCount ?? 0;
    return `${active}/${total} active • imported ${new Date(c.importedAt).toLocaleDateString()}`;
  }

  get selected(): ICompanyListItemDto | null {
    if (!this.value) return null;
    return this.companies.find((c) => c.companyId === this.value) ?? null;
  }
}
