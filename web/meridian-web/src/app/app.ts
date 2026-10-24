import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { UserDto } from './api/models';
import { UsersService } from './api/services';
import { CurrentUser } from './core/current-user';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header>
      <strong>Meridian Vote Tracker</strong>
      <nav>
        <a routerLink="/imports" routerLinkActive="active">Imports</a>
        <a routerLink="/review" routerLinkActive="active">Review</a>
        <a routerLink="/votes" routerLinkActive="active">Votes</a>
        <a routerLink="/snapshots" routerLinkActive="active">Snapshots</a>
      </nav>
      <label class="user">
        Switch user
        <select [value]="currentUser.id()" (change)="switchTo($any($event.target).value)">
          @for (user of users(); track user.id) {
            <option [value]="user.id" [selected]="user.id === currentUser.id()">{{ user.displayName }} · {{ user.role }}</option>
          }
        </select>
      </label>
    </header>
    <main><router-outlet /></main>
  `,
})
export class App implements OnInit {
  protected readonly currentUser = inject(CurrentUser);
  protected readonly users = signal<UserDto[]>([]);
  private readonly usersApi = inject(UsersService);

  ngOnInit(): void {
    this.usersApi.apiUsersGet().subscribe((users) => {
      this.users.set(users);
      this.switchTo(this.currentUser.id(), users);
    });
  }

  protected switchTo(id: string, users = this.users()): void {
    const user = users.find((u) => u.id === id) ?? users[0];
    if (user) {
      this.currentUser.select(user);
    }
  }
}
