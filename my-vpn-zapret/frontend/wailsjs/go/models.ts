export namespace main {
	
	export class V2RayNConfig {
	    server: string;
	    port: number;
	    uuid: string;
	    flow: string;
	    security: string;
	    sni: string;
	    publicKey: string;
	    shortId: string;
	    spiderX: string;
	    encryption: string;
	    remark: string;
	
	    static createFrom(source: any = {}) {
	        return new V2RayNConfig(source);
	    }
	
	    constructor(source: any = {}) {
	        if ('string' === typeof source) source = JSON.parse(source);
	        this.server = source["server"];
	        this.port = source["port"];
	        this.uuid = source["uuid"];
	        this.flow = source["flow"];
	        this.security = source["security"];
	        this.sni = source["sni"];
	        this.publicKey = source["publicKey"];
	        this.shortId = source["shortId"];
	        this.spiderX = source["spiderX"];
	        this.encryption = source["encryption"];
	        this.remark = source["remark"];
	    }
	}

}

