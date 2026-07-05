import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { AuthService } from './auth.service';

export interface DeploymentEventHandlers {
  onLogLine?: (deploymentId: string, providerName: string, sequence: number, line: string) => void;
  onStatusChanged?: (deploymentId: string, providerName: string, status: string) => void;
  onCompleted?: (deploymentId: string, finalStatus: string) => void;
}

@Injectable({ providedIn: 'root' })
export class SignalRService {
  private connection: signalR.HubConnection | null = null;

  constructor(private readonly auth: AuthService) {}

  async connect(handlers: DeploymentEventHandlers): Promise<void> {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      return;
    }

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/deployments', {
        accessTokenFactory: () => this.auth.accessToken ?? ''
      })
      .withAutomaticReconnect()
      .build();

    this.connection.on('LogLine', (deploymentId: string, providerName: string, sequence: number, line: string) => {
      handlers.onLogLine?.(deploymentId, providerName, sequence, line);
    });

    this.connection.on('StatusChanged', (deploymentId: string, providerName: string, status: string) => {
      handlers.onStatusChanged?.(deploymentId, providerName, status);
    });

    this.connection.on('DeploymentCompleted', (deploymentId: string, finalStatus: string) => {
      handlers.onCompleted?.(deploymentId, finalStatus);
    });

    await this.connection.start();
  }

  async joinDeployment(deploymentId: string): Promise<void> {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('JoinDeployment', deploymentId);
    }
  }

  async leaveDeployment(deploymentId: string): Promise<void> {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('LeaveDeployment', deploymentId);
    }
  }

  async disconnect(): Promise<void> {
    if (this.connection) {
      await this.connection.stop();
      this.connection = null;
    }
  }
}
