// A dialog observes a server-owned install; closing it only stops observation.
export class MediaInstallPoller {
  constructor(read,render,error,schedule=setTimeout,unschedule=clearTimeout){Object.assign(this,{read,render,error,schedule,unschedule});this.generation=0;this.active=false;this.timer=null;}
  stop(){this.active=false;this.generation++;this.unschedule(this.timer);this.timer=null;}
  open(status){this.stop();this.active=true;this.show(status);}
  show(status){this.unschedule(this.timer);this.timer=null;this.render(status);if(status.running)this.again();}
  again(){this.unschedule(this.timer);this.timer=this.schedule(()=>this.refresh(),1000);}
  async refresh(){
    if(!this.active)return;const generation=this.generation;this.unschedule(this.timer);this.timer=null;
    try{const status=await this.read();if(this.active&&generation===this.generation)this.show(status);}
    catch(error){if(this.active&&generation===this.generation){this.error(error);this.again();}}
  }
}
