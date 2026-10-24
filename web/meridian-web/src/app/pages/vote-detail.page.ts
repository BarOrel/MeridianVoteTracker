import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Position, VoteDetailsDto, VoteHistoryEntryDto } from '../api/models';
import { VotesService } from '../api/services';
import { CurrentUser } from '../core/current-user';
import { Problem, toProblem } from '../core/problem';

@Component({
  selector: 'app-vote-detail',
  imports: [DatePipe, RouterLink],
  template: `
    <p><a routerLink="/votes">← Votes</a></p>
    @if (vote(); as v) {
      <section>
        <h2>{{ v.assemblyVoteId }} · {{ v.title }}</h2>
        <p class="muted">Session {{ v.session }} · {{ v.voteDate }} · {{ v.countryVotes?.length }} country votes
          @if (v.pendingConflicts) { · <a routerLink="/review"><span class="badge warn">{{ v.pendingConflicts }} pending conflict(s)</span></a> }</p>
      </section>
      <div class="layout">
        <div>
          <section>
            <h3>Northwind position</h3>
            <div class="row">
              <button [class.primary]="position() === 'For'" (click)="position.set(For)">YES (For)</button>
              <button [class.primary]="position() === 'Against'" (click)="position.set(Against)">NO (Against)</button>
            </div>
            <label>Weight (1–5) <input type="number" style="width:70px" [value]="weight()" (input)="weight.set($any($event.target).valueAsNumber)" /></label>
            @if (problem()?.fieldErrors?.['weight']; as errors) { <div class="field-error">{{ errors.join(' ') }}</div> }
            @if (problem(); as p) { @if (!p.fieldErrors['weight']) { <div class="alert bad">{{ p.message }}</div> } }
            <div class="row">
              <button class="primary" [disabled]="!position()" (click)="save(v)">Save position</button>
              @if (!currentUser.isAdmin()) { <span class="muted">Only admins can change positions (enforced by the API).</span> }
            </div>
          </section>
          <section>
            <h3>Country votes</h3>
            <table>
              <tr><th>Code</th><th>Member state</th><th>Choice</th></tr>
              @for (c of v.countryVotes; track c.stateCode) {
                <tr [class.selected]="c.origin === 'ConflictResolution'">
                  <td>{{ c.stateCode }}</td><td>{{ c.stateName }}</td>
                  <td>{{ c.ballot }} @if (c.origin === 'ConflictResolution') { <span class="badge warn" title="changed by {{ c.approvedBy }}">changed by a conflict decision</span> }</td>
                </tr>
              }
            </table>
          </section>
        </div>
        <section>
          <h3>History of this vote</h3>
          <table>
            @for (e of timeline(); track e.id) {
              <tr>
                <td><b>{{ e.type }}</b>
                  <div>{{ describe(e) }}</div>
                  @if (e.newSourceFile) { <div class="muted">source: {{ e.newSourceFile }}{{ e.newSourceRow ? ' row ' + e.newSourceRow : '' }}</div> }
                  @if (e.oldSourceFile) { <div class="muted">previous value from: {{ e.oldSourceFile }}{{ e.oldSourceRow ? ' row ' + e.oldSourceRow : '' }}</div> }
                  @if (e.comment) { <div>“{{ e.comment }}”</div> }
                </td>
                <td class="muted">{{ e.occurredAt | date: 'yyyy-MM-dd HH:mm:ss' }}<div>{{ e.actor }}</div></td>
              </tr>
            }
          </table>
        </section>
      </div>
    }
  `,
})
export class VoteDetailPage implements OnInit {
  readonly id = input.required<string>();
  protected readonly For = Position.For;
  protected readonly Against = Position.Against;
  protected readonly vote = signal<VoteDetailsDto | null>(null);
  protected readonly timeline = signal<VoteHistoryEntryDto[]>([]);
  protected readonly position = signal<Position | null>(null);
  protected readonly weight = signal<number>(3);
  protected readonly problem = signal<Problem | null>(null);
  protected readonly currentUser = inject(CurrentUser);
  private readonly api = inject(VotesService);

  ngOnInit(): void {
    this.load();
  }

  protected save(v: VoteDetailsDto): void {
    this.problem.set(null);
    this.api
      .apiVotesIdPositionPut({ id: v.id!, body: { position: this.position()!, weight: this.weight(), expectedRevision: v.positionRevision! } })
      .subscribe({
        next: () => this.load(),
        error: (error) => {
          const problem = toProblem(error);
          this.problem.set(problem.status === 403 ? { ...problem, message: 'Only an admin can set positions and weights.' } : problem);
          if (problem.status === 409) this.load();
        },
      });
  }

  protected describe(e: VoteHistoryEntryDto): string {
    const target = e.stateCode ?? e.field ?? '';
    if (e.oldValue != null || e.newValue != null) {
      return `${target} ${e.oldValue ?? '—'} → ${e.newValue ?? '—'}`.trim();
    }
    return target;
  }

  private load(): void {
    const id = Number(this.id());
    this.api.apiVotesIdGet({ id }).subscribe((v) => {
      this.vote.set(v);
      this.position.set(v.position ?? null);
      this.weight.set(v.weight ?? 3);
    });
    this.api.apiVotesIdTimelineGet({ id }).subscribe((t) => this.timeline.set(t));
  }
}
