import { Component, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { CompanyFeedComponent } from './pages/company-feed/company-feed.component';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, CompanyFeedComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  protected readonly title = signal('client-insight-web');
}
