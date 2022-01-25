<?xml version="1.0" ?><xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="1.0"><xsl:output method="text" indent="no"/><xsl:template match="/data"><![CDATA[<p>This Microsoft Dynamics CRM duplication-detection task completed successfully.</p>
					<p>Name: ]]><xsl:choose><xsl:when test="asyncoperation/name"><xsl:value-of select="asyncoperation/name" /></xsl:when><xsl:otherwise>[Duplicate Detection task]</xsl:otherwise></xsl:choose><![CDATA[</p>
					<p>Started: ]]><xsl:choose><xsl:when test="asyncoperation/createdon"><xsl:value-of select="asyncoperation/createdon" /></xsl:when><xsl:otherwise></xsl:otherwise></xsl:choose><![CDATA[</p>
					<p>To view and manage duplicates, do one of the following:</p>
					<p>Click <A name="slugAnchorTag"  href="#">https://awesome.crm11.dynamics.com/tools/asyncoperation/edit.aspx?id=]]><xsl:choose><xsl:when test="asyncoperation/asyncoperationid"><xsl:value-of select="asyncoperation/asyncoperationid" /></xsl:when><xsl:otherwise></xsl:otherwise></xsl:choose><![CDATA[</A></p>
					<p>or</p>
					<p>Log on to Microsoft Dynamics CRM and navigate to Duplicate Detection page and double-click the name of the task.</p>
					<p></p>]]></xsl:template></xsl:stylesheet>
				