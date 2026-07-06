import { Component, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../core/services/api.service';
import { DeploymentDetail } from '../core/models/api.models';

@Component({
  selector: 'app-publish-redirect',
  standalone: true,
  template: '<p class="u-text-muted">Taking you to your publish view…</p>'
})
export class PublishRedirectComponent implements OnInit {
  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: ApiService
  ) {}

  ngOnInit(): void {
    const deploymentId = this.route.snapshot.paramMap.get('id') ?? '';
    this.api.getDeployment(deploymentId).subscribe({
      next: (deployment: DeploymentDetail) => {
        void this.router.navigate(['/projects', deployment.projectId, 'deploy', deploymentId], { replaceUrl: true });
      },
      error: () => {
        void this.router.navigate(['/dashboard']);
      }
    });
  }
}
