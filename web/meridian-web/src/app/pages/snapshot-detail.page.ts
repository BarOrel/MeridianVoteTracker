import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SnapshotCountryDto, SnapshotDto } from '../api/models';
import { SnapshotsService } from '../api/services';

@Component({
  selector: 'app-snapshot-detail',
  imports: [RouterLink, DatePipe],
  template: `
    <p><a routerLink="/snapshots">← Snapshots</a></p>
    @if (snapshot(); as s) {
      <section>
        <h2>Snapshot · {{ s.snapshot?.name }}
          @if (s.snapshot?.publishedQuarter) { <span class="badge ok">PUBLISHED {{ s.snapshot?.publishedQuarter }}</span> }</h2>
        <p class="muted">Created by {{ s.snapshot?.createdBy }} {{ s.snapshot?.createdAt | date: 'yyyy-MM-dd HH:mm' }} ·
          {{ s.snapshot?.votes }} votes · {{ s.snapshot?.selection }} · immutable
          @if (s.publishedBy) { · published by {{ s.publishedBy }} {{ s.publishedAt | date: 'yyyy-MM-dd HH:mm' }} }</p>
      </section>
      <div class="layout">
        <section>
          <h3>Ranking</h3>
          <table>
            <tr><th>#</th><th>Member state</th><th>Score</th></tr>
            @for (r of s.ranking; track r.stateCode) {
              <tr class="clickable" [class.selected]="country()?.stateCode === r.stateCode" (click)="open(r.stateCode!)">
                <td>{{ r.rank ?? '—' }}</td><td>{{ r.stateName }}</td><td>{{ r.score ?? 'n/a' }}</td>
              </tr>
            }
          </table>
          <p class="muted">n/a = no countable votes in this snapshot</p>
        </section>
        <section>
          @if (country(); as c) {
            <h3>{{ c.stateName }} · score {{ c.score ?? 'n/a' }} · rank {{ c.rank ?? '—' }}</h3>
            <p class="muted">Score = 100 × matching weights / counted weights = 100 × {{ c.matchingWeight }} / {{ c.countedWeight }}</p>
            <table>
              <tr><th>Vote</th><th>Position</th><th>Weight</th><th>Ballot</th><th>Counted</th><th>Matching</th></tr>
              @for (v of c.votes; track v.assemblyVoteId) {
                <tr><td>{{ v.assemblyVoteId }}</td><td>{{ v.position }}</td><td>{{ v.weight }}</td><td>{{ v.ballot }}</td>
                  <td>{{ v.counted ? v.weight : '—' }}</td><td>{{ v.matching ? v.weight : (v.counted ? 0 : '—') }}</td></tr>
              }
            </table>
          } @else { <p class="muted">Select a state to see how its score was computed.</p> }
          <div class="alert" style="background:#f3f0ff;border-color:#a78bfa">
            <b>Scoring rules applied</b>
            <ul>@for (rule of s.rules; track $index) { <li>{{ rule }}</li> }</ul>
          </div>
        </section>
      </div>
    }
  `,
})
export class SnapshotDetailPage implements OnInit {
  readonly id = input.required<string>();
  protected readonly snapshot = signal<SnapshotDto | null>(null);
  protected readonly country = signal<SnapshotCountryDto | null>(null);
  private readonly api = inject(SnapshotsService);

  ngOnInit(): void {
    this.api.apiSnapshotsIdGet({ id: this.id() }).subscribe((s) => this.snapshot.set(s));
  }

  protected open(code: string): void {
    this.api.apiSnapshotsIdCountriesCodeGet({ id: this.id(), code }).subscribe((c) => this.country.set(c));
  }
}
